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
