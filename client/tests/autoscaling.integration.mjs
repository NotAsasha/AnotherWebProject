import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import path from "node:path";

test(
  "Autoscaler grows within bounds, drains an active Worker and returns to baseline",
  { timeout: 240000 },
  async () => {
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
      "-f",
      "docker-compose.autoscale.yml",
      "--profile",
      "app",
    ];
    const docker = (...args) =>
      execFileSync(dockerPath, args, {
        cwd: root,
        encoding: "utf8",
        timeout: 100000,
      }).trim();
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
          "-At",
          "-v",
          "ON_ERROR_STOP=1",
        ],
        { cwd: root, input: query, encoding: "utf8" },
      ).trim();
    const managed = () =>
      docker(
        "ps",
        "-q",
        "--filter",
        "label=contaminantmigration.autoscaler=contaminantmigration",
      )
        .split(/\r?\n/)
        .filter(Boolean);
    const inspect = (id) => JSON.parse(docker("inspect", id))[0];
    const pause = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
    const users = [];
    const tasks = [];
    async function api(route, method = "GET", body, token) {
      const response = await fetch(`http://localhost:5297/api/v1/${route}`, {
        method,
        headers: {
          "Content-Type": "application/json",
          ...(token ? { Authorization: `Bearer ${token}` } : {}),
        },
        ...(body ? { body: JSON.stringify(body) } : {}),
        signal: AbortSignal.timeout(20000),
      });
      assert.ok(response.ok, `${method} ${route}: ${response.status}`);
      return response.status === 204 ? undefined : response.json();
    }
    try {
      assert.equal(
        sql(
          "SELECT count(*) FROM \"SimulationTasks\" WHERE \"Status\" IN ('Queued','Running','Cancelling');",
        ),
        "0",
        "Finish active tasks first",
      );
      assert.equal(
        managed().length,
        0,
        "Wait for the previous scaling cooldown before testing",
      );
      const baseline = docker(...compose, "ps", "-q", "worker")
        .split(/\r?\n/)
        .filter(Boolean);
      assert.equal(baseline.length, 2);
      assert.ok(docker(...compose, "ps", "-q", "scaler"));
      for (let i = 0; i < 2; i++) {
        const credentials = {
          email: `scaling-${crypto.randomUUID()}@example.com`,
          password: "Scaling-test-password-123",
        };
        const user = await api("auth/register", "POST", credentials);
        user.token = (await api("auth/login", "POST", credentials)).accessToken;
        users.push(user);
      }
      const request = {
        name: "Scaling workload",
        domain: { lengthX: 100, lengthY: 100, nodesX: 512, nodesY: 512 },
        transport: { velocityX: 0.5, velocityY: 0.1, diffusion: 0.1 },
        time: { durationSeconds: 60, timeStepSeconds: 0.008 },
        initialCondition: { peak: 1, centerX: 25, centerY: 50, sigma: 5 },
        requestedFrames: 2,
        boundaryCondition: "ZeroDirichlet",
      };
      for (const user of users)
        for (let i = 0; i < 3; i++)
          tasks.push({
            ...(await api("simulations", "POST", request, user.token)),
            token: user.token,
          });
      let drainedTask;
      let peak = baseline.length;
      let drained = false;
      const deadline = Date.now() + 120000;
      while (Date.now() < deadline) {
        const containers = managed();
        peak = Math.max(peak, baseline.length + containers.length);
        assert.ok(
          baseline.length + containers.length <= 4,
          "Autoscaler exceeded its configured maximum",
        );
        const states = await Promise.all(
          tasks.map((task) =>
            api(`simulations/${task.id}`, "GET", undefined, task.token),
          ),
        );
        assert.ok(
          states.every(
            (state) =>
              !["Failed", "TimedOut", "Cancelled"].includes(state.status),
          ),
        );
        if (!drained)
          for (const id of containers) {
            const container = inspect(id);
            const active = states.find(
              (state) =>
                state.status === "Running" &&
                state.workerId?.startsWith(`${container.Config.Hostname}:`),
            );
            if (active) {
              // Token stays inside the container; only the drain response is returned.
              const denied = docker(
                "exec",
                id,
                "curl",
                "-s",
                "-o",
                "/dev/null",
                "-w",
                "%{http_code}",
                "-X",
                "POST",
                "http://localhost:8080/internal/worker",
              );
              assert.equal(denied, "401");
              const response = docker(
                "exec",
                id,
                "sh",
                "-c",
                'curl -fsS -H "Authorization: Bearer $WorkerManagement__Token" -X POST http://localhost:8080/internal/worker',
              );
              assert.equal(JSON.parse(response).draining, true);
              drainedTask = await api(
                `simulations/${active.id}`,
                "GET",
                undefined,
                tasks.find((task) => task.id === active.id).token,
              );
              assert.equal(
                drainedTask.status,
                "Completed",
                "Drain must wait for the active result commit",
              );
              assert.equal(
                drainedTask.attempt,
                1,
                "Drain must not interrupt and retry active computations",
              );
              drained = true;
              break;
            }
          }
        if (states.every((state) => state.status === "Completed")) break;
        await pause(250);
      }
      assert.ok(peak > baseline.length, "No automatic scale-up observed");
      assert.ok(
        drained,
        "No active managed Worker was observed for the drain check",
      );
      for (const task of tasks) {
        const state = await api(
          `simulations/${task.id}`,
          "GET",
          undefined,
          task.token,
        );
        assert.equal(state.status, "Completed");
        assert.equal(state.attempt, 1);
        assert.equal(
          (
            await api(
              `simulations/${task.id}/result`,
              "GET",
              undefined,
              task.token,
            )
          ).frames.length,
          2,
        );
      }
      const cooldownDeadline = Date.now() + 90000;
      while (Date.now() < cooldownDeadline && managed().length)
        await pause(1000);
      assert.equal(
        managed().length,
        0,
        "Idle managed Workers must be drained and removed",
      );
      for (const id of baseline)
        assert.equal(
          inspect(id).State.Running,
          true,
          "Baseline Workers must be preserved",
        );
      console.log(
        `Passed: ${baseline.length} -> ${peak} -> ${baseline.length} Workers; six tasks completed on attempt 1; active drain completed task ${drainedTask.id}.`,
      );
    } finally {
      for (const user of users) {
        assert.match(user.id, /^[\da-f-]{36}$/i);
        const owned = await api(
          "simulations?page=1&pageSize=50",
          "GET",
          undefined,
          user.token,
        );
        for (const task of owned.items)
          if (["Queued", "Running", "Cancelling"].includes(task.status))
            await api(
              `simulations/${task.id}/cancel`,
              "POST",
              undefined,
              user.token,
            );
        const deadline = Date.now() + 45000;
        const active = () =>
          sql(
            `SELECT count(*) FROM "SimulationTasks" WHERE "OwnerId"='${user.id}' AND "Status" IN ('Queued','Running','Cancelling');`,
          );
        while (Date.now() < deadline && active() !== "0") await pause(250);
        assert.equal(active(), "0", "Retaining active fixtures for diagnosis");
        sql(
          `DELETE FROM "SimulationTasks" WHERE "OwnerId"='${user.id}'; DELETE FROM "SubmissionKeys" WHERE "OwnerId"='${user.id}'; DELETE FROM "Users" WHERE "Id"='${user.id}';`,
        );
      }
    }
  },
);
