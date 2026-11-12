<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue";
import { Play, Pause } from "@lucide/vue";
import { api, response } from "../lib/api";
import { decodeFrame, pixels } from "../lib/heatmap";
import {
  active,
  queueWaitMessage,
  number,
  statusNames,
  type Result,
  type SimulationTask,
} from "../lib/types";
const props = defineProps<{ task: SimulationTask | null }>();
const canvas = ref<HTMLCanvasElement>();
const metadata = ref<Result | null>(null);
const frameIndex = ref(0),
  loading = ref(false),
  error = ref(""),
  playing = ref(false);
const hover = ref<{ x: number; y: number; value: number } | null>(null);
const current = computed(() => metadata.value?.frames[frameIndex.value]);
const maximum = computed(() =>
  Math.max(
    0,
    ...(metadata.value?.frames.map((frame) => frame.maximumConcentration) ??
      []),
  ),
);
const currentValues = ref<Float32Array | null>(null);
const cache = new Map<number, Float32Array>();
let controller: AbortController | undefined;
let epoch = 0,
  requestId = 0;
const timer = setInterval(() => {
  if (!playing.value || loading.value || !metadata.value) return;
  if (frameIndex.value >= metadata.value.frames.length - 1)
    playing.value = false;
  else frameIndex.value++;
}, 350);
function draw() {
  if (!canvas.value || !metadata.value || !currentValues.value) return;
  canvas.value.width = metadata.value.nodesX;
  canvas.value.height = metadata.value.nodesY;
  canvas.value
    .getContext("2d")
    ?.putImageData(
      new ImageData(
        pixels(
          currentValues.value,
          metadata.value.nodesX,
          metadata.value.nodesY,
          maximum.value,
        ),
        metadata.value.nodesX,
        metadata.value.nodesY,
      ),
      0,
      0,
    );
}
async function loadFrame() {
  const result = metadata.value,
    task = props.task;
  if (!result || !task) return;
  const requested = frameIndex.value,
    generation = epoch,
    request = ++requestId;
  controller?.abort();
  controller = new AbortController();
  loading.value = true;
  error.value = "";
  hover.value = null;
  try {
    let values = cache.get(requested);
    if (!values) {
      const payload = await response(
        `simulations/${task.id}/frames/${requested}`,
        { signal: controller.signal },
      );
      values = decodeFrame(
        await payload.arrayBuffer(),
        result.nodesX,
        result.nodesY,
      );
      if (generation !== epoch || request !== requestId) return;
      if (cache.size >= 8) cache.delete(cache.keys().next().value!);
      cache.set(requested, values);
    }
    if (generation !== epoch || request !== requestId) return;
    currentValues.value = values;
    draw();
  } catch (failure) {
    if (failure instanceof Error && failure.name === "AbortError") return;
    if (generation === epoch && request === requestId) {
      error.value =
        failure instanceof Error
          ? failure.message
          : "Не вдалося завантажити кадр.";
      playing.value = false;
    }
  } finally {
    if (generation === epoch && request === requestId) loading.value = false;
  }
}
async function loadResult() {
  const generation = ++epoch;
  ++requestId;
  controller?.abort();
  controller = new AbortController();
  metadata.value = null;
  currentValues.value = null;
  playing.value = false;
  error.value = "";
  hover.value = null;
  cache.clear();
  if (props.task?.status !== "Completed") {
    loading.value = false;
    return;
  }
  loading.value = true;
  try {
    const result = await api<Result>(`simulations/${props.task.id}/result`, {
      signal: controller.signal,
    });
    if (generation !== epoch) return;
    if (result.format !== "float32-le" || !result.frames.length)
      throw new Error("Формат результату не підтримується.");
    metadata.value = result;
    frameIndex.value = result.frames.length - 1;
  } catch (failure) {
    if (failure instanceof Error && failure.name === "AbortError") return;
    if (generation === epoch)
      error.value =
        failure instanceof Error ? failure.message : "Результат недоступний.";
  } finally {
    if (generation === epoch) loading.value = false;
  }
  if (generation === epoch) await loadFrame();
}
watch([() => props.task?.id, () => props.task?.status], loadResult, {
  immediate: true,
});
watch(frameIndex, () => {
  void loadFrame();
});
watch(canvas, draw);
function toggle() {
  if (!metadata.value) return;
  if (frameIndex.value >= metadata.value.frames.length - 1)
    frameIndex.value = 0;
  playing.value = !playing.value;
}
function inspect(event: MouseEvent) {
  if (!currentValues.value || !metadata.value || !props.task || !canvas.value)
    return;
  const rect = canvas.value.getBoundingClientRect(),
    { nodesX: nx, nodesY: ny } = metadata.value;
  const i = Math.min(
    nx - 1,
    Math.max(0, Math.floor(((event.clientX - rect.left) / rect.width) * nx)),
  );
  const j =
    ny -
    1 -
    Math.min(
      ny - 1,
      Math.max(0, Math.floor(((event.clientY - rect.top) / rect.height) * ny)),
    );
  hover.value = {
    x: (i * props.task.parameters.domain.lengthX) / (nx - 1),
    y: (j * props.task.parameters.domain.lengthY) / (ny - 1),
    value: currentValues.value[j * nx + i]!,
  };
}
onBeforeUnmount(() => {
  epoch++;
  controller?.abort();
  clearInterval(timer);
});
</script>
<template>
  <section class="panel result-panel">
    <div class="panel-heading">
      <h2>Результат</h2>
      <span v-if="task" class="muted mono"
        >{{ task.parameters.domain.nodesX }} ×
        {{ task.parameters.domain.nodesY }}</span
      >
    </div>
    <div v-if="metadata && !error" class="plot-section">
      <div class="plot-meta">
        <span>Кадр {{ frameIndex + 1 }} / {{ metadata.frames.length }}</span
        ><span v-if="loading" role="status">Завантаження…</span>
      </div>
      <div
        class="plot-chart"
        :style="{
          width: `min(100%, ${(400 * task!.parameters.domain.lengthX) / task!.parameters.domain.lengthY + 70}px)`,
        }"
      >
        <div class="plot-layout">
          <div class="y-axis">
            <span>{{ number(task!.parameters.domain.lengthY) }}</span><b>y, м</b
            ><span>0</span>
          </div>
          <div class="plot-viewport">
            <canvas
              ref="canvas"
              data-testid="concentration-canvas"
              aria-label="Карта концентрації домішки"
              :style="{
                aspectRatio: `${task!.parameters.domain.lengthX} / ${task!.parameters.domain.lengthY}`,
              }"
              @mousemove="inspect"
              @mouseleave="hover = null"
            ></canvas>
          </div>
          <div class="color-scale">
            <span>{{ number(maximum, 3) }}</span>
            <div class="color-gradient"></div>
            <span>0</span>
          </div>
        </div>
        <div class="plot-bottom">
          <span>0</span><span>x, м</span
          ><span>{{ number(task!.parameters.domain.lengthX) }}</span>
        </div>
      </div>
      <div class="plot-readout">
        <span v-if="hover" class="mono"
          >x {{ number(hover.x) }} · y {{ number(hover.y) }} · C
          {{ number(hover.value, 5) }}</span
        ><span v-else>Концентрація</span><span>мг/м³</span>
      </div>
      <div class="playback">
        <button
          class="icon-button"
          :aria-label="playing ? 'Призупинити анімацію' : 'Відтворити анімацію'"
          @click="toggle"
        >
          <Pause v-if="playing" :size="18" /><Play v-else :size="18" /></button
        ><input
          v-model.number="frameIndex"
          type="range"
          min="0"
          :max="metadata.frames.length - 1"
          step="1"
          aria-label="Часова шкала"
          @input="playing = false"
        /><span class="mono playback-time"
          >{{ number(current?.timeSeconds ?? 0, 1) }} /
          {{ number(task!.parameters.time.durationSeconds) }} с</span
        >
      </div>
    </div>
    <div v-else class="result-placeholder">
      <template v-if="error"
        ><p role="alert">{{ error }}</p>
        <button class="button secondary" @click="loadResult">
          Повторити
        </button></template
      >
      <p v-else-if="loading" role="status">Завантаження…</p>
      <template v-else-if="task && active(task.status)"
        ><h3>{{ statusNames[task.status] }}</h3>
        <p
          v-if="task.status === 'Queued'"
          data-testid="queue-wait"
          class="muted"
        >
          {{ queueWaitMessage(task.estimatedWaitSeconds) }}
        </p>
        <div v-else class="large-progress">
          <div :style="{ width: `${task.progressPercent}%` }"></div>
        </div>
        <span v-if="task.status !== 'Queued'" class="mono"
          >{{ number(task.progressPercent, 0) }}% ·
          {{ number(task.completedSteps, 0) }} /
          {{ number(task.totalSteps, 0) }} кроків</span
        ></template
      >
      <template v-else-if="task"
        ><h3>{{ statusNames[task.status] }}</h3>
        <p v-if="task.status === 'TimedOut'">
          Зменште сітку або тривалість розрахунку.
        </p>
        <p v-else-if="task.status === 'Failed'">
          Спробуйте запустити розрахунок знову.
        </p></template
      >
      <p v-else>Запустіть симуляцію або виберіть задачу з історії.</p>
    </div>
  </section>
</template>
