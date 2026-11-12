<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from "vue";
import { api, ApiError } from "../lib/api";
import { number, statusNames, type TaskStatus } from "../lib/types";

interface ContainerMetric {
  id: string;
  name: string;
  service: string;
  state: string;
  health: string | null;
  cpuPercent: number | null;
  memoryBytes: number | null;
  memoryLimitBytes: number | null;
  managed: boolean;
}
interface Overview {
  asOfUtc: string;
  metricsStale: boolean;
  monitoring: {
    collectedAtUtc: string;
    containers: ContainerMetric[];
    scaling: {
      minWorkers: number;
      maxWorkers: number;
      currentWorkers: number;
      pollSeconds: number;
      idleSeconds: number;
    };
  } | null;
  tasks: Record<TaskStatus, number>;
  activeTasks: {
    id: string;
    name: string;
    status: TaskStatus;
    progressPercent: number;
    createdAt: string;
    workerId: string | null;
  }[];
}
const overview = ref<Overview | null>(null);
const loading = ref(false);
const error = ref("");
let timer: ReturnType<typeof setInterval> | undefined;
let controller: AbortController | undefined;
let alive = true;
async function refresh() {
  if (loading.value) return;
  loading.value = true;
  controller = new AbortController();
  try {
    const value = await api<Overview>("admin/overview", {
      signal: controller.signal,
    });
    if (alive) {
      overview.value = value;
      error.value = "";
    }
  } catch (failure) {
    if (!alive) return;
    error.value =
      failure instanceof Error ? failure.message : "Не вдалося оновити огляд.";
    if (failure instanceof ApiError && [401, 403].includes(failure.status)) {
      overview.value = null;
      clearInterval(timer);
    }
  } finally {
    if (alive) loading.value = false;
  }
}
const state = (value: string) =>
  ({
    running: "Працює",
    exited: "Зупинено",
    restarting: "Перезапуск",
    paused: "Призупинено",
    created: "Створено",
    dead: "Помилка",
  })[value] ?? value;
const health = (value: string | null) =>
  value
    ? ({ healthy: "Готовий", unhealthy: "Неготовий", starting: "Запускається" }[
        value
      ] ?? value)
    : "—";
const memory = (value: number | null) =>
  value === null ? "—" : number(value / 1024 ** 2, 1);
const time = (value: string) => new Date(value).toLocaleTimeString("uk-UA");
onMounted(() => {
  void refresh();
  timer = setInterval(() => void refresh(), 5000);
});
onBeforeUnmount(() => {
  alive = false;
  clearInterval(timer);
  controller?.abort();
});
</script>

<template>
  <section class="admin-overview">
    <div class="admin-heading">
      <h2>Моніторинг</h2>
      <button class="button secondary" :disabled="loading" @click="refresh">
        Оновити
      </button>
    </div>
    <p v-if="error" class="error-box" role="alert">{{ error }}</p>
    <p v-if="!overview && loading" role="status">Завантаження…</p>
    <template v-if="overview">
      <dl class="admin-summary">
        <div>
          <dt>У черзі</dt>
          <dd>{{ overview.tasks.Queued }}</dd>
        </div>
        <div>
          <dt>Обчислення</dt>
          <dd>{{ overview.tasks.Running }}</dd>
        </div>
        <div>
          <dt>Скасування</dt>
          <dd>{{ overview.tasks.Cancelling }}</dd>
        </div>
        <div>
          <dt>Worker / максимум</dt>
          <dd>
            {{ overview.monitoring?.scaling.currentWorkers ?? "—" }} /
            {{ overview.monitoring?.scaling.maxWorkers ?? "—" }}
          </dd>
        </div>
      </dl>
      <p v-if="overview.metricsStale" class="error-box" role="status">
        {{
          overview.monitoring
            ? "Метрики контейнерів застаріли."
            : "Метрики контейнерів недоступні. Перевірте сервіс scaler."
        }}
      </p>
      <section class="panel">
        <div class="panel-heading">
          <h3>Контейнери</h3>
          <span v-if="overview.monitoring" class="muted">{{
            time(overview.monitoring.collectedAtUtc)
          }}</span>
        </div>
        <div class="table-scroll">
          <table aria-label="Контейнери системи">
            <thead>
              <tr>
                <th>Контейнер</th>
                <th>Стан</th>
                <th>Готовність</th>
                <th title="100% відповідає одному CPU">CPU, %</th>
                <th>RAM / ліміт, MiB</th>
              </tr>
            </thead>
            <tbody>
              <tr
                v-for="container in overview.monitoring?.containers ?? []"
                :key="container.id"
              >
                <td class="container-name">
                  {{ container.name
                  }}<small v-if="container.managed">Додатковий Worker</small>
                </td>
                <td>{{ state(container.state) }}</td>
                <td>{{ health(container.health) }}</td>
                <td class="mono">
                  {{
                    container.cpuPercent === null
                      ? "—"
                      : number(container.cpuPercent, 1)
                  }}
                </td>
                <td class="mono">
                  {{ memory(container.memoryBytes) }} /
                  {{ memory(container.memoryLimitBytes) }}
                </td>
              </tr>
              <tr v-if="!overview.monitoring?.containers.length">
                <td colspan="5">Дані відсутні.</td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>
      <section class="panel">
        <div class="panel-heading">
          <h3>Активні задачі ({{ overview.activeTasks.length }})</h3>
        </div>
        <div class="table-scroll">
          <table aria-label="Активні задачі системи">
            <thead>
              <tr>
                <th>Задача</th>
                <th>Стан</th>
                <th>Прогрес</th>
                <th>Worker</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="task in overview.activeTasks" :key="task.id">
                <td class="container-name">{{ task.name }}</td>
                <td>{{ statusNames[task.status] }}</td>
                <td>{{ number(task.progressPercent, 0) }}%</td>
                <td class="container-name mono">
                  {{ task.workerId?.split(":")[0] ?? "—" }}
                </td>
              </tr>
              <tr v-if="!overview.activeTasks.length">
                <td colspan="4">Активних задач немає.</td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>
    </template>
  </section>
</template>
