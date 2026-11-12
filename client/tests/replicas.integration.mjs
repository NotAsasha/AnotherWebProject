import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import path from "node:path";
import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";

// Requires docker-compose.replicas.yml, two healthy APIs and two Workers; no active tasks.
test(
  "Two APIs: balancing, shared JWT/data, Redis delivery, parallel Workers and API failover",
  { timeout: 180000 },
  async () => {
    const base = "http://localhost:5297";
    const apiUrls = ["http://localhost:5179", "http://localhost:5180"];
    const dockerPath = path.join(
      process.env.LOCALAPPDATA,
      "Programs/DockerDesktop/resources/bin/docker.exe",
    );
    const root = path.resolve("..");
    const compose = [
      "compose",
      "-f",
      "docker-compose.yml",
      "-f",
      "docker-compose.replicas.yml",
      "--profile",
      "app",
    ];
    const docker = (...args) =>
      execFileSync(dockerPath, args, {
        cwd: root,
        encoding: "utf8",
        timeout: 150000,
      });
    const sql = (query) =>
      execFileSync(
        dockerPath,
        [
          ...compose,
          "exec",
          "-T",
          "postgres",
          "psql",
          "-U",
          "contaminant",
          "-d",
          "contaminantmigration",
          "-v",
          "ON_ERROR_STOP=1",
          "-At",
        ],
        { cwd: root, input: query, encoding: "utf8", timeout: 20000 },
      ).trim();
    let userId;
    let token;
    let stopped = false;
    const connections = [];
    const tasks = [];
    const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
    async function api(route, method = "GET", body, origin = base) {
      const response = await fetch(`${origin}/api/v1/${route}`, {
        method,
        headers: {
          "Content-Type": "application/json",
          ...(token ? { Authorization: `Bearer ${token}` } : {}),
        },
        ...(body ? { body: JSON.stringify(body) } : {}),
        signal: AbortSignal.timeout(20000),
      });
      assert.ok(
        response.ok,
        `${method} ${route}: ${response.status} ${await (!response.ok ? response.text() : Promise.resolve(""))}`,
      );
      return response.status === 204 ? undefined : response.json();
    }
    function hub(origin) {
      const updates = [];
      const connection = new HubConnectionBuilder()
        .withUrl(`${origin}/hubs/simulations`, {
          accessTokenFactory: () => token,
        })
        .configureLogging(LogLevel.None)
        .build();
      connection.on("SimulationUpdated", (value) => updates.push(value));
      connections.push(connection);
      return { connection, updates };
    }
    try {
      assert.equal(
        sql(
          "SELECT count(*) FROM \"SimulationTasks\" WHERE \"Status\" IN ('Queued','Running','Cancelling');",
        ),
        "0",
        "Finish active tasks before testing replicas",
      );
      assert.equal(
        docker(...compose, "ps", "-q", "worker")
          .trim()
          .split(/\r?\n/)
          .filter(Boolean).length,
        2,
        "Start exactly two Workers",
      );
      const instances = await Promise.all(
        apiUrls.map(async (origin) => {
          const response = await fetch(`${origin}/health/ready`);
          assert.equal(response.status, 200);
          return response.headers.get("x-service-instance");
        }),
      );
      assert.ok(instances.every(Boolean));
      assert.equal(new Set(instances).size, 2);
      const credentials = {
        email: `replicas-${crypto.randomUUID()}@example.com`,
        password: "Replica-check-password-123",
      };
      userId = (await api("auth/register", "POST", credentials)).id;
      token = (await api("auth/login", "POST", credentials)).accessToken;
      for (const origin of apiUrls)
        assert.equal(
          (await api("auth/me", "GET", undefined, origin)).id,
          userId,
        );

      const negotiation = await fetch(
        `${base}/hubs/simulations/negotiate?negotiateVersion=1`,
        { method: "POST", headers: { Authorization: `Bearer ${token}` } },
      );
      assert.equal(negotiation.status, 200);
      const cookie = negotiation.headers
        .getSetCookie()
        .map((value) => value.split(";")[0])
        .join("; ");
      assert.match(cookie, /ContaminantMigration.Affinity=/);
      const servedBy = new Set();
      for (let i = 0; i < 8; i++) {
        const response = await fetch(`${base}/api/v1/auth/me`, {
          headers: { Authorization: `Bearer ${token}`, Cookie: cookie },
        });
        assert.equal(response.status, 200);
        servedBy.add(response.headers.get("x-service-instance"));
      }
      assert.deepEqual(
        servedBy,
        new Set(instances),
        "REST must balance even with a SignalR affinity cookie",
      );

      const listeners = [...apiUrls, base].map(hub);
      await Promise.all(
        listeners.map((listener) => listener.connection.start()),
      );
      const request = {
        name: "Replica workload",
        domain: { lengthX: 100, lengthY: 100, nodesX: 512, nodesY: 512 },
        transport: { velocityX: 0.5, velocityY: 0.1, diffusion: 0.1 },
        time: { durationSeconds: 60, timeStepSeconds: 0.01 },
        initialCondition: { peak: 1, centerX: 25, centerY: 50, sigma: 5 },
        requestedFrames: 2,
        boundaryCondition: "ZeroDirichlet",
      };
      await api("simulations/validate", "POST", request);
      // Sequential HTTP admission avoids exceeding the per-user quota; execution remains parallel.
      for (let i = 0; i < 3; i++)
        tasks.push(
          await api("simulations", "POST", {
            ...request,
            name: `Replica workload ${i + 1}`,
          }),
        );
      await Promise.all(
        listeners.flatMap((listener) =>
          tasks.map((task) => listener.connection.invoke("Subscribe", task.id)),
        ),
      );
      const deadline = Date.now() + 90000;
      while (
        Date.now() < deadline &&
        !listeners.every((listener) =>
          tasks.every((task) =>
            listener.updates.some(
              (value) =>
                value.taskId === task.id && value.status === "Completed",
            ),
          ),
        )
      )
        await delay(100);
      for (const listener of listeners) {
        for (const task of tasks)
          assert.ok(
            listener.updates.some(
              (value) =>
                value.taskId === task.id &&
                value.status === "Completed" &&
                value.progressPercent === 100,
            ),
            "Every API connection must receive completion via Redis, without REST polling",
          );
        assert.ok(
          listener.updates.some(
            (value) => value.progressPercent > 0 && value.progressPercent < 100,
          ),
          "Intermediate progress must reach each API",
        );
      }
      const completed = await Promise.all(
        tasks.map((task) => api(`simulations/${task.id}`)),
      );
      assert.ok(
        completed.every(
          (task) => task.status === "Completed" && task.attempt === 1,
        ),
      );
      assert.equal(
        new Set(completed.map((task) => task.workerId.split(":")[0])).size,
        2,
        "Both Worker containers must execute tasks",
      );
      assert.ok(
        completed.some((a, i) =>
          completed.some(
            (b, j) =>
              i !== j &&
              Date.parse(a.startedAt) < Date.parse(b.finishedAt) &&
              Date.parse(b.startedAt) < Date.parse(a.finishedAt),
          ),
        ),
        "Execution intervals must overlap",
      );
      for (const task of tasks)
        for (const origin of apiUrls)
          assert.equal(
            (
              await api(
                `simulations/${task.id}/result`,
                "GET",
                undefined,
                origin,
              )
            ).frames.length,
            2,
          );
      console.log(
        "Passed: two APIs balance authenticated REST; both Workers execute concurrently; Redis delivers progress/completion to both API instances and YARP.",
      );

      // Failover of fresh requests/subscriptions once YARP's active probe removes API2.
      await listeners[2].connection.stop();
      stopped = true;
      docker(...compose, "stop", "api2");
      let healthy = false;
      for (let i = 0; i < 45; i++) {
        try {
          const responses = await Promise.all(
            Array.from({ length: 4 }, () =>
              fetch(`${base}/api/v1/auth/me`, {
                headers: { Authorization: `Bearer ${token}` },
                signal: AbortSignal.timeout(2000),
              }),
            ),
          );
          healthy = responses.every(
            (response) =>
              response.status === 200 &&
              response.headers.get("x-service-instance") === instances[0],
          );
          if (healthy) break;
        } catch {
          /* active health probes converge */
        }
        await delay(1000);
      }
      assert.ok(healthy, "YARP must route to the surviving API");
      await listeners[2].connection.start();
      const before = listeners[2].updates.length;
      await listeners[2].connection.invoke("Subscribe", tasks[0].id);
      assert.ok(
        listeners[2].updates
          .slice(before)
          .some(
            (value) =>
              value.taskId === tasks[0].id && value.status === "Completed",
          ),
        "New subscription through survivor must restore the snapshot",
      );
      console.log(
        "Passed: API2 outage leaves authenticated REST and new SignalR subscriptions available through API1.",
      );
    } finally {
      await Promise.allSettled(
        connections.map((connection) => connection.stop()),
      );
      try {
        if (stopped)
          docker(
            ...compose,
            "up",
            "-d",
            "--no-deps",
            "--wait",
            "--wait-timeout",
            "120",
            "api2",
          );
      } finally {
        if (userId) {
          assert.match(userId, /^[\da-f-]{36}$/i);
          // Cancel only this fixture's tasks, then wait for consumers to release them before deletion.
          const owned = await api(
            "simulations?page=1&pageSize=50",
            "GET",
            undefined,
            apiUrls[0],
          );
          for (const task of owned.items)
            if (["Queued", "Running", "Cancelling"].includes(task.status))
              await api(
                `simulations/${task.id}/cancel`,
                "POST",
                undefined,
                apiUrls[0],
              );
          const cleanupDeadline = Date.now() + 45000;
          while (
            Date.now() < cleanupDeadline &&
            sql(
              `SELECT count(*) FROM "SimulationTasks" WHERE "OwnerId"='${userId}' AND "Status" IN ('Queued','Running','Cancelling');`,
            ) !== "0"
          )
            await delay(250);
          assert.equal(
            sql(
              `SELECT count(*) FROM "SimulationTasks" WHERE "OwnerId"='${userId}' AND "Status" IN ('Queued','Running','Cancelling');`,
            ),
            "0",
            "Retaining active fixtures for diagnosis",
          );
          sql(
            `DELETE FROM "SimulationTasks" WHERE "OwnerId"='${userId}'; DELETE FROM "SubmissionKeys" WHERE "OwnerId"='${userId}'; DELETE FROM "Users" WHERE "Id"='${userId}';`,
          );
        }
      }
    }
  },
);
