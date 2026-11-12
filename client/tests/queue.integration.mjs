import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import path from "node:path";
import fs from "node:fs";
import os from "node:os";

test(
  "Saturated Workers expose queue estimates, preserve ownership and clear ETA on completion",
  { timeout: 180000 },
  async () => {
    const root = path.resolve("..");
    const dockerPath = path.join(
      process.env.LOCALAPPDATA,
      "Programs/DockerDesktop/resources/bin/docker.exe",
    );
    const compose = [
      "compose",
      "-f",
      "docker-compose.yml",
      "-f",
      "docker-compose.replicas.yml",
      "-f",
      "docker-compose.autoscale.yml",
    ];
    const override = path.join(
      os.tmpdir(),
      `queue-check-${crypto.randomUUID()}.yml`,
    );
    const docker = (...args) =>
      execFileSync(dockerPath, args, {
        cwd: root,
        encoding: "utf8",
        timeout: 140000,
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
    const users = [],
      tasks = [];
    const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
    let changed = false;
    async function api(route, method = "GET", body, token, expected = 200) {
      const response = await fetch(`http://localhost:5297/api/v1/${route}`, {
        method,
        headers: {
          "Content-Type": "application/json",
          ...(token ? { Authorization: `Bearer ${token}` } : {}),
        },
        ...(body ? { body: JSON.stringify(body) } : {}),
        signal: AbortSignal.timeout(20000),
      });
      assert.equal(
        response.status,
        expected,
        `${method} ${route}: ${await (response.status !== expected ? response.text() : Promise.resolve(""))}`,
      );
      return expected === 404 ? undefined : response.json();
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
        docker(
          "ps",
          "-q",
          "--filter",
          "label=contaminantmigration.autoscaler=contaminantmigration",
        ),
        "",
        "Wait for managed Workers to retire",
      );
      assert.equal(
        docker(...compose, "ps", "-q", "worker")
          .split(/\r?\n/)
          .filter(Boolean).length,
        2,
      );
      fs.writeFileSync(
        override,
        'services:\n  scaler:\n    environment:\n      Scaling__MaxWorkers: "2"\n',
      );
      changed = true;
      docker(
        ...compose,
        "-f",
        override,
        "--profile",
        "app",
        "up",
        "-d",
        "--no-deps",
        "--wait",
        "--wait-timeout",
        "120",
        "scaler",
      );
      for (let i = 0; i < 2; i++) {
        const credentials = {
          email: `queue-${crypto.randomUUID()}@example.com`,
          password: "Queue-check-password-123",
        };
        const user = await api(
          "auth/register",
          "POST",
          credentials,
          undefined,
          201,
        );
        users.push(user);
        user.token = (await api("auth/login", "POST", credentials)).accessToken;
      }
      const request = {
        name: "Queue estimate check",
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
            ...(await api("simulations", "POST", request, user.token, 202)),
            token: user.token,
          });
      await api(
        `simulations/${tasks[0].id}`,
        "GET",
        undefined,
        users[1].token,
        404,
      );
      let observed = false;
      const deadline = Date.now() + 90000;
      while (Date.now() < deadline) {
        const states = (
          await Promise.all(
            users.map((user) =>
              api(
                "simulations?page=1&pageSize=50",
                "GET",
                undefined,
                user.token,
              ),
            ),
          )
        ).flatMap((page) => page.items);
        assert.ok(
          states.every(
            (task) =>
              !["TimedOut", "Failed", "Cancelled"].includes(task.status),
          ),
        );
        const waiting = states.find(
          (task) =>
            task.status === "Queued" &&
            Number.isFinite(task.estimatedWaitSeconds) &&
            task.estimatedWaitSeconds > 0,
        );
        if (waiting) {
          const owner = tasks.find((task) => task.id === waiting.id);
          const detail = await api(
            `simulations/${waiting.id}`,
            "GET",
            undefined,
            owner.token,
          );
          if (detail.status === "Queued" && detail.estimatedWaitSeconds > 0) {
            observed = true;
            console.log(
              `Observed queued task: approximately ${detail.estimatedWaitSeconds}s until execution.`,
            );
          }
        }
        if (
          states.length === 6 &&
          states.every((task) => task.status === "Completed")
        )
          break;
        await delay(300);
      }
      assert.ok(
        observed,
        "No positive wait estimate observed while Workers were saturated",
      );
      for (const task of tasks) {
        const state = await api(
          `simulations/${task.id}`,
          "GET",
          undefined,
          task.token,
        );
        assert.equal(state.status, "Completed");
        assert.equal(state.estimatedWaitSeconds, null);
      }
      console.log(
        "Passed: queued ETA visible in list and detail; foreign task hidden; completed tasks have no ETA.",
      );
    } finally {
      try {
        for (const user of users) {
          assert.match(user.id, /^[\da-f-]{36}$/i);
          if (user.token) {
            const owned = await api(
              "simulations?page=1&pageSize=50",
              "GET",
              undefined,
              user.token,
            );
            for (const task of owned.items)
              if (["Queued", "Running", "Cancelling"].includes(task.status)) {
                const response = await fetch(
                  `http://localhost:5297/api/v1/simulations/${task.id}/cancel`,
                  {
                    method: "POST",
                    headers: { Authorization: `Bearer ${user.token}` },
                  },
                );
                assert.ok(response.ok);
              }
          }
          const active = () =>
            sql(
              `SELECT count(*) FROM "SimulationTasks" WHERE "OwnerId"='${user.id}' AND "Status" IN ('Queued','Running','Cancelling');`,
            );
          const deadline = Date.now() + 45000;
          while (Date.now() < deadline && active() !== "0") await delay(250);
          assert.equal(
            active(),
            "0",
            "Retaining active fixtures for diagnosis",
          );
          sql(
            `DELETE FROM "SimulationTasks" WHERE "OwnerId"='${user.id}'; DELETE FROM "SubmissionKeys" WHERE "OwnerId"='${user.id}'; DELETE FROM "Users" WHERE "Id"='${user.id}';`,
          );
        }
      } finally {
        if (changed)
          docker(
            ...compose,
            "--profile",
            "app",
            "up",
            "-d",
            "--no-deps",
            "--wait",
            "--wait-timeout",
            "120",
            "scaler",
          );
        if (fs.existsSync(override)) fs.unlinkSync(override);
      }
    }
  },
);
