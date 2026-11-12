import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import path from "node:path";
import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";

test(
  "Authenticated subscriptions, owner isolation, completion push and reconnect snapshot",
  { timeout: 40000 },
  async () => {
    const base = process.env.TEST_API_URL ?? "http://localhost:5297";
    const ids = [];
    const connections = [];
    async function api(route, method = "GET", body, token) {
      const response = await fetch(`${base}/api/v1/${route}`, {
        method,
        headers: {
          "Content-Type": "application/json",
          ...(token ? { Authorization: `Bearer ${token}` } : {}),
        },
        ...(body ? { body: JSON.stringify(body) } : {}),
      });
      assert.ok(response.ok, `${route}: HTTP ${response.status}`);
      return response.json();
    }
    async function user() {
      const credentials = {
        email: `hub-${crypto.randomUUID()}@example.com`,
        password: "Realtime-password-123",
      };
      ids.push((await api("auth/register", "POST", credentials)).id);
      return (await api("auth/login", "POST", credentials)).accessToken;
    }
    const connect = (token) =>
      new HubConnectionBuilder()
        .withUrl(`${base}/hubs/simulations`, {
          accessTokenFactory: () => token,
        })
        .configureLogging(LogLevel.None)
        .build();
    try {
      const anonymous = connect("");
      connections.push(anonymous);
      await assert.rejects(anonymous.start());
      const owner = await user();
      const outsider = await user();
      const connection = connect(owner);
      const foreign = connect(outsider);
      connections.push(connection, foreign);
      const updates = [];
      connection.on("SimulationUpdated", (value) => updates.push(value));
      await connection.start();
      await foreign.start();
      const task = await api(
        "simulations",
        "POST",
        {
          name: "Hub integration",
          domain: { lengthX: 100, lengthY: 100, nodesX: 101, nodesY: 101 },
          transport: { velocityX: 0.5, velocityY: 0.1, diffusion: 0.1 },
          time: { durationSeconds: 60 },
          initialCondition: { peak: 1, centerX: 25, centerY: 50, sigma: 5 },
          requestedFrames: 2,
          boundaryCondition: "ZeroDirichlet",
        },
        owner,
      );
      await assert.rejects(
        foreign.invoke("Subscribe", task.id),
        /Task not found/,
      );
      await connection.invoke("Subscribe", task.id);
      assert.ok(
        updates.some((value) => value.taskId === task.id),
        "Subscription must include a current snapshot",
      );
      for (
        let i = 0;
        i < 100 && !updates.some((value) => value.status === "Completed");
        i++
      )
        await new Promise((resolve) => setTimeout(resolve, 100));
      assert.ok(
        updates.some(
          (value) =>
            value.taskId === task.id &&
            value.status === "Completed" &&
            value.progressPercent === 100,
        ),
      );
      await connection.stop();
      await connection.start();
      const before = updates.length;
      await connection.invoke("Subscribe", task.id);
      assert.ok(
        updates.length > before && updates.at(-1).status === "Completed",
        "Reconnect must support resubscription and a fresh snapshot",
      );
      const ignoredQuery = await fetch(
        `${base}/api/v1/auth/me?access_token=${owner}`,
      );
      assert.equal(
        ignoredQuery.status,
        401,
        "Query tokens are only accepted on the Hub route",
      );
      await connection.invoke("Unsubscribe", task.id);
    } finally {
      await Promise.allSettled(
        connections.map((connection) => connection.stop()),
      );
      if (ids.length) {
        const values = ids
          .map((id) => {
            assert.match(id, /^[\da-f-]{36}$/i);
            return `'${id}'`;
          })
          .join(",");
        const docker = path.join(
          process.env.LOCALAPPDATA,
          "Programs/DockerDesktop/resources/bin/docker.exe",
        );
        execFileSync(
          docker,
          [
            "compose",
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
          ],
          {
            cwd: path.resolve(".."),
            input: `DELETE FROM "SimulationTasks" WHERE "OwnerId" IN (${values}); DELETE FROM "SubmissionKeys" WHERE "OwnerId" IN (${values}); DELETE FROM "Users" WHERE "Id" IN (${values});`,
            stdio: ["pipe", "ignore", "pipe"],
          },
        );
      }
    }
  },
);
