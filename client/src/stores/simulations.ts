import { computed, ref } from "vue";
import { defineStore } from "pinia";
import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from "@microsoft/signalr";
import { accessToken, api, ApiError } from "../lib/api";
import {
  active,
  type CreateTask,
  type Limits,
  type Plan,
  type SimulationTask,
  type TaskPage,
  type Update,
} from "../lib/types";

export const useSimulations = defineStore("simulations", () => {
  const items = ref<SimulationTask[]>([]);
  const selected = ref<SimulationTask | null>(null);
  const total = ref(0);
  const page = ref(1);
  const filter = ref("");
  const limits = ref<Limits | null>(null);
  const loading = ref(false);
  const busy = ref(false);
  const error = ref("");
  const connection = ref<"connecting" | "live" | "polling">("connecting");
  const activeCount = computed(
    () => items.value.filter((task) => active(task.status)).length,
  );
  let hub: HubConnection | undefined;
  let polling: ReturnType<typeof setInterval> | undefined;
  let generation = 0;
  let refreshing = false;
  let listRequest = 0;
  let selectionRequest = 0;
  const subscribed = new Set<string>();

  function apply(value: SimulationTask) {
    const previous = items.value.find((task) => task.id === value.id);
    if (previous && previous.version > value.version) return;
    items.value = items.value.map((task) =>
      task.id === value.id ? value : task,
    );
    if (
      selected.value?.id === value.id &&
      selected.value.version <= value.version
    )
      selected.value = value;
  }
  async function subscriptions() {
    const current = hub;
    const epoch = generation;
    if (current?.state !== HubConnectionState.Connected) return;
    const desired = new Set(items.value.map((task) => task.id));
    if (selected.value) desired.add(selected.value.id);
    try {
      for (const id of [...subscribed])
        if (!desired.has(id)) {
          await current.invoke("Unsubscribe", id);
          if (epoch !== generation || current !== hub) return;
          subscribed.delete(id);
        }
      for (const id of desired)
        if (!subscribed.has(id)) {
          await current.invoke("Subscribe", id);
          if (epoch !== generation || current !== hub) return;
          subscribed.add(id);
        }
    } catch {
      /* A concurrent delete or reconnect is repaired by REST polling. */
    }
  }
  async function load(showLoading = false) {
    const currentGeneration = generation;
    const requestId = ++listRequest;
    if (showLoading) loading.value = true;
    try {
      const result = await api<TaskPage>(
        `simulations?page=${page.value}&pageSize=20${filter.value ? `&status=${filter.value}` : ""}`,
      );
      if (currentGeneration !== generation || requestId !== listRequest) return;
      items.value = result.items.map((value) => {
        const previous = items.value.find((task) => task.id === value.id);
        return previous && previous.version > value.version ? previous : value;
      });
      total.value = result.totalCount;
      if (selected.value) {
        const latest = items.value.find(
          (task) => task.id === selected.value?.id,
        );
        if (latest) apply(latest);
        else {
          const selectedId = selected.value.id;
          try {
            const value = await api<SimulationTask>(
              `simulations/${selectedId}`,
            );
            if (currentGeneration === generation) apply(value);
          } catch (failure) {
            if (failure instanceof ApiError && failure.status === 404) {
              if (
                currentGeneration === generation &&
                selected.value?.id === selectedId
              )
                selected.value = null;
            } else throw failure;
          }
        }
      }
      await subscriptions();
    } finally {
      if (requestId === listRequest) loading.value = false;
    }
  }
  async function refresh() {
    if (refreshing) return;
    const epoch = generation;
    refreshing = true;
    try {
      await load();
      if (epoch === generation) error.value = "";
    } catch (failure) {
      if (epoch === generation) error.value = message(failure);
    } finally {
      if (epoch === generation) refreshing = false;
    }
  }
  async function select(id: string) {
    const epoch = generation;
    const request = ++selectionRequest;
    try {
      const value = await api<SimulationTask>(`simulations/${id}`);
      if (epoch !== generation || request !== selectionRequest) return;
      selected.value = value;
      sessionStorage.setItem("contaminant.selected", id);
      await subscriptions();
    } catch (failure) {
      if (epoch === generation && request === selectionRequest)
        error.value = message(failure);
    }
  }
  async function start() {
    const epoch = ++generation;
    error.value = "";
    connection.value = "connecting";
    polling = setInterval(() => {
      if (epoch !== generation) return;
      void refresh();
      if (hub?.state === HubConnectionState.Disconnected) void connect(epoch);
    }, 3000);
    try {
      const configuration = await api<Limits>("simulation-limits");
      if (epoch !== generation) return;
      limits.value = configuration;
      await load(true);
      if (epoch !== generation) return;
      const previous = sessionStorage.getItem("contaminant.selected");
      if (previous) await select(previous);
      else if (items.value[0]) await select(items.value[0].id);
    } catch (failure) {
      if (epoch === generation) error.value = message(failure);
    }
    if (epoch !== generation) return;
    hub = new HubConnectionBuilder()
      .withUrl("/hubs/simulations", { accessTokenFactory: accessToken })
      .withAutomaticReconnect([0, 1000, 3000, 10000])
      .configureLogging(LogLevel.None)
      .build();
    hub.on("SimulationUpdated", (update: Update) => {
      if (epoch !== generation) return;
      const current =
        selected.value?.id === update.taskId
          ? selected.value
          : items.value.find((task) => task.id === update.taskId);
      if (!current || update.version <= current.version) return;
      apply({ ...current, ...update, id: update.taskId });
      if (!active(update.status)) void refresh();
    });
    hub.onreconnecting(() => {
      if (epoch === generation) connection.value = "polling";
    });
    hub.onclose(() => {
      if (epoch === generation) connection.value = "polling";
    });
    hub.onreconnected(async () => {
      if (epoch !== generation) return;
      subscribed.clear();
      connection.value = "live";
      await refresh();
      await subscriptions();
    });
    await connect(epoch);
  }
  async function connect(epoch: number) {
    if (
      !hub ||
      hub.state !== HubConnectionState.Disconnected ||
      epoch !== generation
    )
      return;
    try {
      await hub.start();
      if (epoch !== generation) return;
      connection.value = "live";
      subscribed.clear();
      await subscriptions();
      await refresh();
    } catch {
      if (epoch === generation) connection.value = "polling";
    }
  }
  async function stop() {
    generation++;
    selectionRequest++;
    listRequest++;
    clearInterval(polling);
    const previous = hub;
    hub = undefined;
    subscribed.clear();
    items.value = [];
    selected.value = null;
    limits.value = null;
    total.value = 0;
    page.value = 1;
    filter.value = "";
    error.value = "";
    refreshing = false;
    loading.value = false;
    busy.value = false;
    await previous?.stop();
  }
  async function submit(request: CreateTask): Promise<string> {
    const epoch = generation;
    const body = JSON.stringify(request);
    let pending: { key: string; body: string } | null = null;
    try {
      pending = JSON.parse(
        sessionStorage.getItem("contaminant.pending") ?? "null",
      );
    } catch {
      /* reset malformed retry state */
    }
    if (!pending || pending.body !== body)
      pending = { key: crypto.randomUUID(), body };
    sessionStorage.setItem("contaminant.pending", JSON.stringify(pending));
    const result = await api<{ id: string }>("simulations", {
      method: "POST",
      body,
      headers: { "Idempotency-Key": pending.key },
    });
    if (epoch !== generation) return result.id;
    sessionStorage.removeItem("contaminant.pending");
    page.value = 1;
    filter.value = "";
    sessionStorage.setItem("contaminant.selected", result.id);
    // A failed follow-up read must not turn an accepted POST into a failed submission.
    try {
      await load();
    } catch {
      if (epoch === generation)
        error.value =
          "Задачу прийнято. Не вдалося оновити список — спробуйте ще раз.";
    }
    if (epoch !== generation) return result.id;
    await select(result.id);
    return result.id;
  }
  async function validate(request: CreateTask) {
    return api<Plan>("simulations/validate", {
      method: "POST",
      body: JSON.stringify(request),
    });
  }
  async function cancel(id: string) {
    const epoch = generation;
    busy.value = true;
    try {
      const result = await api<SimulationTask>(`simulations/${id}/cancel`, {
        method: "POST",
      });
      if (epoch !== generation) return;
      apply(result);
      await load();
    } catch (failure) {
      if (epoch === generation) error.value = message(failure);
    } finally {
      if (epoch === generation) busy.value = false;
    }
  }
  async function remove(id: string) {
    const epoch = generation;
    busy.value = true;
    try {
      await api(`simulations/${id}`, { method: "DELETE" });
      if (epoch !== generation) return;
      if (selected.value?.id === id) {
        selected.value = null;
        sessionStorage.removeItem("contaminant.selected");
      }
      if (items.value.length === 1 && page.value > 1) page.value--;
      await load();
    } catch (failure) {
      if (epoch === generation) error.value = message(failure);
    } finally {
      if (epoch === generation) busy.value = false;
    }
  }
  const message = (failure: unknown) =>
    failure instanceof Error ? failure.message : "Не вдалося виконати дію.";
  return {
    items,
    selected,
    total,
    page,
    filter,
    limits,
    loading,
    busy,
    error,
    connection,
    activeCount,
    start,
    stop,
    load,
    refresh,
    select,
    submit,
    validate,
    cancel,
    remove,
  };
});
