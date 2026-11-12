import { test, expect } from "@playwright/test";
import { execFileSync } from "node:child_process";
import path from "node:path";
import fs from "node:fs";

const users: string[] = [];
test.afterAll(() => {
  if (!users.length) return;
  const docker = path.join(
    process.env.LOCALAPPDATA!,
    "Programs/DockerDesktop/resources/bin/docker.exe",
  );
  const ids = users
    .map((id) => {
      if (!/^[\da-f-]{36}$/i.test(id)) throw new Error("Invalid fixture ID");
      return `'${id}'`;
    })
    .join(",");
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
      input: `DELETE FROM "SimulationTasks" WHERE "OwnerId" IN (${ids}); DELETE FROM "SubmissionKeys" WHERE "OwnerId" IN (${ids}); DELETE FROM "Users" WHERE "Id" IN (${ids});`,
      stdio: ["pipe", "ignore", "pipe"],
    },
  );
});

test("Register, run, inspect frames, reload history and log out", async ({
  page,
}) => {
  const errors: string[] = [];
  const sockets: string[] = [];
  page.on("websocket", (socket) => sockets.push(socket.url()));
  let submissions = 0;
  let successfulReads = 0;
  let accepted = false;
  let failedRead = false;
  page.on("response", (response) => {
    if (
      response.url().includes("/api/v1/simulations?page=") &&
      response.status() === 200
    )
      successfulReads++;
    if (
      response.url().endsWith("/api/v1/simulations") &&
      response.request().method() === "POST"
    ) {
      submissions++;
      accepted = response.status() === 202;
    }
  });
  await page.route(/\/api\/v1\/simulations\?page=/, (route) => {
    if (accepted && !failedRead) {
      failedRead = true;
      return route.fulfill({
        status: 503,
        contentType: "application/problem+json",
        body: '{"title":"Temporarily unavailable"}',
      });
    }
    return route.continue();
  });
  page.on("pageerror", (error) => errors.push(error.message));
  await page.goto("/");
  await page
    .getByRole("button", { name: "Зареєструватися", exact: true })
    .click();
  await page
    .getByLabel("Електронна пошта", { exact: true })
    .fill(`browser-${crypto.randomUUID()}@example.com`);
  await page.getByLabel("Пароль", { exact: true }).fill("Browser-password-123");
  const registration = page.waitForResponse(
    (response) =>
      response.url().endsWith("/auth/register") &&
      response.request().method() === "POST",
  );
  await page
    .getByRole("button", { name: "Створити обліковий запис", exact: true })
    .click();
  users.push((await (await registration).json()).id);
  await expect(
    page.getByRole("heading", { name: "Міграція домішки" }),
  ).toBeVisible();
  await expect(page.locator(".topbar")).toHaveAttribute(
    "data-connection",
    "live",
  );
  if (new URL(page.url()).protocol === "https:") {
    expect(
      sockets.some((url) => url.startsWith("wss://localhost:5443/hubs/")),
    ).toBe(true);
    expect(sockets.every((url) => url.startsWith("wss:"))).toBe(true);
    const affinity = (await page.context().cookies()).find(
      (cookie) => cookie.name === "ContaminantMigration.Affinity",
    );
    expect(affinity?.secure).toBe(true);
    expect(affinity?.httpOnly).toBe(true);
  }
  const validationError =
    "Simulation exceeds 200000000 estimated cell updates.";
  await page.route(
    "**/api/v1/simulations/validate",
    (route) =>
      route.fulfill({
        status: 400,
        contentType: "application/problem+json",
        body: JSON.stringify({ errors: { domain: [validationError] } }),
      }),
    { times: 1 },
  );
  await page
    .getByRole("button", { name: "Запустити симуляцію", exact: true })
    .click();
  await expect(page.getByRole("alert")).toHaveText(validationError);
  expect(submissions).toBe(0);
  await page
    .getByRole("button", { name: "Запустити симуляцію", exact: true })
    .click();
  const canvas = page.getByTestId("concentration-canvas");
  await expect(canvas).toBeVisible({ timeout: 30000 });
  expect(submissions).toBe(1);
  expect(failedRead).toBe(true);
  await expect(
    page.getByRole("slider", { name: "Часова шкала" }),
  ).toHaveAttribute("max", "15");
  await page.getByRole("slider", { name: "Часова шкала" }).focus();
  await page.getByRole("slider", { name: "Часова шкала" }).press("Home");
  await expect(page.locator(".playback-time")).toContainText("0 /");
  await expect
    .poll(async () =>
      canvas.evaluate((node: HTMLCanvasElement) => [
        ...node.getContext("2d")!.getImageData(25, 50, 1, 1).data,
      ]),
    )
    .toEqual([253, 231, 37, 255]);
  const readsBefore = successfulReads;
  await expect
    .poll(() => successfulReads, { timeout: 6000 })
    .toBeGreaterThan(readsBefore);
  await expect(page.getByRole("slider", { name: "Часова шкала" })).toHaveValue(
    "0",
  );
  const first = await canvas.evaluate((node: HTMLCanvasElement) =>
    node.toDataURL(),
  );
  await page.getByRole("slider", { name: "Часова шкала" }).press("End");
  await expect
    .poll(async () =>
      canvas.evaluate((node: HTMLCanvasElement) => node.toDataURL()),
    )
    .not.toBe(first);
  await page
    .getByRole("button", { name: "Відтворити анімацію", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "Призупинити анімацію", exact: true }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "Призупинити анімацію", exact: true })
    .click();
  fs.mkdirSync("node_modules/.cache", { recursive: true });
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({
    path: "node_modules/.cache/laboratory-desktop.png",
    fullPage: true,
  });
  await page.reload();
  await expect(canvas).toBeVisible({ timeout: 30000 });
  await page.getByRole("button", { name: "Історія", exact: true }).click();
  await expect(page.getByRole("heading", { name: /^Історія/ })).toBeVisible();
  await page.locator(".task-name").click();
  await expect(canvas).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(
    page.getByRole("button", { name: "Запустити симуляцію", exact: true }),
  ).toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
  await page.screenshot({
    path: "node_modules/.cache/laboratory-mobile.png",
    fullPage: true,
  });
  await page
    .getByRole("button", { name: "Вийти з облікового запису", exact: true })
    .click();
  await expect(page.getByRole("heading", { name: "Вхід" })).toBeVisible();
  expect(
    await page.evaluate(() => sessionStorage.getItem("contaminant.session")),
  ).toBeNull();
  expect(errors).toEqual([]);
});

test("Queue estimates refresh while task version is unchanged", async ({
  page,
  request,
}) => {
  const credentials = {
    email: `queue-ui-${crypto.randomUUID()}@example.com`,
    password: "Browser-password-123",
  };
  const registration = await request.post("/api/v1/auth/register", {
    data: credentials,
  });
  expect(registration.status()).toBe(201);
  users.push((await registration.json()).id);
  const task = {
    id: crypto.randomUUID(),
    name: "Queued simulation",
    status: "Queued",
    progressPercent: 0,
    completedSteps: 0,
    totalSteps: 67,
    version: 1,
    attempt: 0,
    errorCode: null,
    createdAt: new Date().toISOString(),
    startedAt: null,
    finishedAt: null,
    estimatedWaitSeconds: null as number | null,
    parameters: {
      domain: { lengthX: 100, lengthY: 100, nodesX: 101, nodesY: 101 },
    },
  };
  await page.route(/\/api\/v1\/simulations(?:\?|\/)/, (route) => {
    const list = route.request().url().includes("?");
    return route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(
        list ? { items: [task], page: 1, pageSize: 20, totalCount: 1 } : task,
      ),
    });
  });
  await page.goto("/");
  await page
    .getByLabel("Електронна пошта", { exact: true })
    .fill(credentials.email);
  await page.getByLabel("Пароль", { exact: true }).fill(credentials.password);
  await page.getByRole("button", { name: "Увійти", exact: true }).click();
  await expect(page.getByTestId("queue-wait")).toHaveText(
    "Час очікування уточнюється.",
  );
  task.estimatedWaitSeconds = 25;
  await expect(page.getByTestId("queue-wait")).toHaveText(
    "Початок приблизно через 25 с.",
    { timeout: 7000 },
  );
  task.estimatedWaitSeconds = 90;
  await expect(page.getByTestId("queue-wait")).toHaveText(
    "Початок приблизно через 2 хв.",
    { timeout: 7000 },
  );
  task.status = "Running";
  task.version++;
  await expect(page.getByTestId("queue-wait")).not.toBeVisible({
    timeout: 7000,
  });
});

test("Admin monitoring enforces role, shows metrics and clears data after role revocation", async ({
  page,
  request,
}) => {
  const docker = path.join(
    process.env.LOCALAPPDATA!,
    "Programs/DockerDesktop/resources/bin/docker.exe",
  );
  const run = (args: string[], input?: string) =>
    execFileSync(docker, ["compose", ...args], {
      cwd: path.resolve(".."),
      input,
      encoding: "utf8",
      timeout: 30000,
      stdio: ["pipe", "pipe", "pipe"],
    });
  expect((await request.get("/api/v1/admin/overview")).status()).toBe(401);
  const credentials = {
    email: `admin-ui-${crypto.randomUUID()}@example.com`,
    password: "Browser-password-123",
  };
  const registration = await request.post("/api/v1/auth/register", {
    data: { ...credentials, role: "Admin" },
  });
  expect(registration.status()).toBe(201);
  const user = await registration.json();
  users.push(user.id);
  expect(user.role).toBe("User");
  const oldSession = await (
    await request.post("/api/v1/auth/login", { data: credentials })
  ).json();
  const oldHeaders = { Authorization: `Bearer ${oldSession.accessToken}` };
  expect(
    (
      await request.get("/api/v1/admin/overview", { headers: oldHeaders })
    ).status(),
  ).toBe(403);
  await page.goto("/");
  await page
    .getByLabel("Електронна пошта", { exact: true })
    .fill(credentials.email);
  await page.getByLabel("Пароль", { exact: true }).fill(credentials.password);
  await page.getByRole("button", { name: "Увійти", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "Адміністрування", exact: true }),
  ).not.toBeVisible();
  run([
    "exec",
    "-T",
    "api",
    "dotnet",
    "ContaminantMigration.WebApi.dll",
    `--Admin:PromoteEmail=${credentials.email}`,
  ]);
  expect(
    (
      await request.get("/api/v1/admin/overview", { headers: oldHeaders })
    ).status(),
  ).toBe(403);
  await page.getByRole("button", { name: "Вийти з облікового запису" }).click();
  await page
    .getByLabel("Електронна пошта", { exact: true })
    .fill(credentials.email);
  await page.getByLabel("Пароль", { exact: true }).fill(credentials.password);
  await page.getByRole("button", { name: "Увійти", exact: true }).click();
  await page
    .getByRole("button", { name: "Адміністрування", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "Моніторинг", exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("table", { name: "Контейнери системи" }),
  ).toBeVisible();
  const session = await page.evaluate(() =>
    JSON.parse(sessionStorage.getItem("contaminant.session")!),
  );
  const headers = { Authorization: `Bearer ${session.accessToken}` };
  let data: any;
  await expect
    .poll(
      async () => {
        const response = await request.get("/api/v1/admin/overview", {
          headers,
        });
        expect(response.status()).toBe(200);
        expect(response.headers()["cache-control"]).toBe("no-store");
        data = await response.json();
        return data.monitoring !== null && !data.metricsStale;
      },
      { timeout: 20000 },
    )
    .toBe(true);
  expect(
    data.monitoring.containers.some((c: any) => c.service === "worker"),
  ).toBe(true);
  expect(
    data.monitoring.containers.some(
      (c: any) => typeof c.cpuPercent === "number" && c.memoryBytes > 0,
    ),
  ).toBe(true);
  for (const c of data.monitoring.containers)
    expect(Object.keys(c).sort()).toEqual(
      [
        "id",
        "name",
        "service",
        "state",
        "health",
        "cpuPercent",
        "memoryBytes",
        "memoryLimitBytes",
        "managed",
      ].sort(),
    );
  const job = await request.post("/api/v1/simulations", {
    headers,
    data: {
      name: "Admin monitoring workload",
      domain: { lengthX: 100, lengthY: 100, nodesX: 512, nodesY: 512 },
      transport: { velocityX: 0.5, velocityY: 0.1, diffusion: 0.1 },
      time: { durationSeconds: 60, timeStepSeconds: 0.008 },
      initialCondition: { peak: 1, centerX: 25, centerY: 50, sigma: 5 },
      requestedFrames: 2,
      boundaryCondition: "ZeroDirichlet",
    },
  });
  expect(job.status()).toBe(202);
  const taskId = (await job.json()).id;
  const active = await (
    await request.get("/api/v1/admin/overview", { headers })
  ).json();
  expect(active.activeTasks.some((t: any) => t.id === taskId)).toBe(true);
  expect(active.tasks.Queued + active.tasks.Running).toBeGreaterThan(0);
  await page.getByRole("button", { name: "Оновити", exact: true }).click();
  await expect(
    page.getByRole("table", { name: "Активні задачі системи" }),
  ).toContainText("Admin monitoring workload");
  await page.screenshot({
    path: "node_modules/.cache/admin-desktop.png",
    fullPage: true,
  });
  await page.setViewportSize({ width: 390, height: 844 });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
  await page.screenshot({
    path: "node_modules/.cache/admin-mobile.png",
    fullPage: true,
  });
  await expect
    .poll(
      async () =>
        (
          await (
            await request.get(`/api/v1/simulations/${taskId}`, { headers })
          ).json()
        ).status,
      { timeout: 30000 },
    )
    .toBe("Completed");
  let mocked = { ...data, metricsStale: true };
  await page.route("**/api/v1/admin/overview", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(mocked),
    }),
  );
  await page.getByRole("button", { name: "Оновити", exact: true }).click();
  await expect(
    page.getByText("Метрики контейнерів застаріли.", { exact: true }),
  ).toBeVisible();
  mocked = { ...mocked, monitoring: null };
  await page.getByRole("button", { name: "Оновити", exact: true }).click();
  await expect(
    page.getByText("Метрики контейнерів недоступні. Перевірте сервіс scaler.", {
      exact: true,
    }),
  ).toBeVisible();
  await page.unroute("**/api/v1/admin/overview");
  expect(user.id).toMatch(/^[\da-f-]{36}$/i);
  run(
    [
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
    `UPDATE "Users" SET "Role"='User' WHERE "Id"='${user.id}';`,
  );
  expect(
    (await request.get("/api/v1/admin/overview", { headers })).status(),
  ).toBe(403);
  await page.getByRole("button", { name: "Оновити", exact: true }).click();
  await expect(
    page.getByRole("table", { name: "Контейнери системи" }),
  ).not.toBeVisible();
  await expect(page.getByRole("alert")).toContainText("Недостатньо прав.");
});

test("HTTPS entry redirects HTTP and preserves path and query", async ({
  page,
  request,
  baseURL,
}) => {
  test.skip(
    !baseURL?.startsWith("https:"),
    "Requires the HTTPS Compose configuration",
  );
  const redirect = await request.get(
    "http://localhost:5173/history?view=recent",
    { maxRedirects: 0 },
  );
  expect(redirect.status()).toBe(308);
  expect(redirect.headers().location).toBe(
    "https://localhost:5443/history?view=recent",
  );
  const response = await page.goto("http://localhost:5173/");
  await expect(page).toHaveURL("https://localhost:5443/");
  expect((await response!.securityDetails())?.protocol).toMatch(/^TLS/);
  expect(await page.evaluate(() => window.isSecureContext)).toBe(true);
  const health = await request.get("/health");
  expect(health.status()).toBe(200);
});

test("Initial signup screen is usable on a phone", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/");
  await expect(page.getByRole("button", { name: "Увійти" })).toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
});

test("Queued tasks can be cancelled and deleted through the interface", async ({
  page,
  request,
}) => {
  const docker = path.join(
    process.env.LOCALAPPDATA!,
    "Programs/DockerDesktop/resources/bin/docker.exe",
  );
  const run = (args: string[]) =>
    execFileSync(docker, ["compose", "--profile", "app", ...args], {
      cwd: path.resolve(".."),
      stdio: "ignore",
    });
  const credentials = {
    email: `cancel-ui-${crypto.randomUUID()}@example.com`,
    password: "Browser-password-123",
  };
  const registration = await request.post("/api/v1/auth/register", {
    data: credentials,
  });
  expect(registration.status()).toBe(201);
  users.push((await registration.json()).id);
  run(["stop", "worker"]);
  try {
    await page.goto("/");
    await page
      .getByLabel("Електронна пошта", { exact: true })
      .fill(credentials.email);
    await page.getByLabel("Пароль", { exact: true }).fill(credentials.password);
    await page.getByRole("button", { name: "Увійти", exact: true }).click();
    await page
      .getByRole("button", { name: "Запустити симуляцію", exact: true })
      .click();
    await expect(page.locator(".result-placeholder h3")).toHaveText("У черзі");
    await page.getByRole("button", { name: "Скасувати", exact: true }).click();
    await expect(page.locator(".result-placeholder h3")).toHaveText(
      "Скасовано",
    );
    await page.getByRole("button", { name: "Історія", exact: true }).click();
    await page
      .getByRole("button", { name: "Видалити симуляцію", exact: true })
      .click();
    await expect(page.getByRole("dialog")).toBeVisible();
    await page.getByRole("button", { name: "Залишити", exact: true }).click();
    await expect(page.getByRole("dialog")).not.toBeVisible();
    await page
      .getByRole("button", { name: "Видалити симуляцію", exact: true })
      .click();
    await page.getByRole("button", { name: "Видалити", exact: true }).click();
    await expect(page.getByText("Задач немає.", { exact: true })).toBeVisible();
  } finally {
    run(["up", "-d", "--wait", "--wait-timeout", "120", "worker"]);
  }
});

test("REST polling keeps results available when SignalR cannot connect", async ({
  page,
  request,
}) => {
  const credentials = {
    email: `poll-ui-${crypto.randomUUID()}@example.com`,
    password: "Browser-password-123",
  };
  const registration = await request.post("/api/v1/auth/register", {
    data: credentials,
  });
  expect(registration.status()).toBe(201);
  users.push((await registration.json()).id);
  await page.route("**/hubs/**", (route) => route.abort());
  await page.goto("/");
  await page
    .getByLabel("Електронна пошта", { exact: true })
    .fill(credentials.email);
  await page.getByLabel("Пароль", { exact: true }).fill(credentials.password);
  await page.getByRole("button", { name: "Увійти", exact: true }).click();
  await expect(page.locator(".topbar")).toHaveAttribute(
    "data-connection",
    "polling",
  );
  await page
    .getByRole("button", { name: "Запустити симуляцію", exact: true })
    .click();
  await expect(page.getByTestId("concentration-canvas")).toBeVisible({
    timeout: 20000,
  });
  await page.evaluate(() => {
    const session = JSON.parse(sessionStorage.getItem("contaminant.session")!);
    session.expiresAt = "2000-01-01T00:00:00Z";
    sessionStorage.setItem("contaminant.session", JSON.stringify(session));
  });
  await page.reload();
  await expect(page.getByRole("heading", { name: "Вхід" })).toBeVisible();
  expect(
    await page.evaluate(() => sessionStorage.getItem("contaminant.session")),
  ).toBeNull();
});
