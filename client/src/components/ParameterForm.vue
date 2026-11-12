<script setup lang="ts">
import { reactive, ref, watch } from "vue";
import NumberField from "./NumberField.vue";
import { defaultTask, number, type SimulationTask } from "../lib/types";
import { useSimulations } from "../stores/simulations";
import { ApiError } from "../lib/api";
const jobs = useSimulations();
const emit = defineEmits<{ created: [] }>();
const form = reactive(defaultTask());
const element = ref<HTMLFormElement>();
const error = ref("");
const busy = ref(false);
watch(
  form,
  () => {
    error.value = "";
  },
  { deep: true },
);
function reuse(task: SimulationTask) {
  Object.assign(
    form,
    JSON.parse(JSON.stringify({ ...task.parameters, name: task.name })),
  );
}
defineExpose({ reuse });
function localError() {
  const { domain: d, initialCondition: g, time: t, transport: v } = form;
  const values = [
    d.lengthX,
    d.lengthY,
    d.nodesX,
    d.nodesY,
    g.peak,
    g.centerX,
    g.centerY,
    g.sigma,
    t.durationSeconds,
    v.velocityX,
    v.velocityY,
    v.diffusion,
    form.requestedFrames,
  ];
  if (values.some((value) => !Number.isFinite(value)))
    return "Заповніть усі числові поля.";
  if (
    g.centerX <= 0 ||
    g.centerX >= d.lengthX ||
    g.centerY <= 0 ||
    g.centerY >= d.lengthY
  )
    return "Центр плями має бути всередині області.";
  const dx = d.lengthX / (d.nodesX - 1),
    dy = d.lengthY / (d.nodesY - 1);
  if (
    g.sigma < Math.max(dx, dy) ||
    g.sigma > Math.min(d.lengthX, d.lengthY) / 2
  )
    return `Розмір плями σ має бути від ${number(Math.max(dx, dy))} до ${number(Math.min(d.lengthX, d.lengthY) / 2)} м.`;
  if (t.timeStepSeconds !== null) {
    const rate =
      Math.abs(v.velocityX) / dx +
      Math.abs(v.velocityY) / dy +
      2 * v.diffusion * (1 / dx ** 2 + 1 / dy ** 2);
    if (
      !Number.isFinite(t.timeStepSeconds) ||
      t.timeStepSeconds <= 0 ||
      t.timeStepSeconds * rate > 1
    )
      return "Часовий крок нестійкий. Зменште його або ввімкніть автоматичний вибір.";
  }
  return "";
}
async function perform() {
  if (!element.value?.reportValidity() || busy.value) return;
  error.value = localError();
  if (error.value) return;
  busy.value = true;
  const snapshot = JSON.parse(JSON.stringify(form));
  try {
    await jobs.validate(snapshot);
    await jobs.submit(snapshot);
    emit("created");
  } catch (failure) {
    error.value =
      failure instanceof Error
        ? failure.message
        : "Не вдалося запустити симуляцію.";
    if (failure instanceof ApiError) {
      const details = [...new Set(Object.values(failure.fields).flat())];
      if (details.length) error.value = details.join(" ");
    }
  } finally {
    busy.value = false;
  }
}
</script>
<template>
  <section class="panel parameter-panel">
    <div class="panel-heading"><h2>Параметри</h2></div>
    <form ref="element" @submit.prevent="perform">
      <fieldset :disabled="busy">
        <label class="name-field"
          >Назва<input v-model="form.name" maxlength="100" required
        /></label>
        <section class="form-section">
          <h3>Область і сітка</h3>
          <div class="field-grid">
            <NumberField
              v-model="form.domain.lengthX"
              label="Довжина X"
              unit="м"
              :min="0.001"
              :max="jobs.limits?.numerical.maxDomainLength ?? 10000"
            />
            <NumberField
              v-model="form.domain.lengthY"
              label="Довжина Y"
              unit="м"
              :min="0.001"
              :max="jobs.limits?.numerical.maxDomainLength ?? 10000"
            />
            <NumberField
              v-model="form.domain.nodesX"
              label="Вузли X"
              :min="3"
              :max="jobs.limits?.numerical.maxNodesPerAxis ?? 512"
              :step="1"
            />
            <NumberField
              v-model="form.domain.nodesY"
              label="Вузли Y"
              :min="3"
              :max="jobs.limits?.numerical.maxNodesPerAxis ?? 512"
              :step="1"
            />
          </div>
        </section>
        <section class="form-section">
          <h3>Потік і дифузія</h3>
          <div class="field-grid">
            <NumberField
              v-model="form.transport.velocityX"
              label="Швидкість X"
              unit="м/с"
              :min="-100"
              :max="100"
            />
            <NumberField
              v-model="form.transport.velocityY"
              label="Швидкість Y"
              unit="м/с"
              :min="-100"
              :max="100"
            />
            <NumberField
              v-model="form.transport.diffusion"
              label="Дифузія D"
              unit="м²/с"
              :min="0"
              :max="100"
            />
          </div>
        </section>
        <section class="form-section">
          <h3>Час</h3>
          <div class="field-grid">
            <NumberField
              v-model="form.time.durationSeconds"
              label="Тривалість"
              unit="с"
              :min="0.001"
              :max="86400"
            />
            <NumberField
              v-model="form.requestedFrames"
              label="Кадри"
              :min="2"
              :max="jobs.limits?.numerical.maxFrames ?? 32"
              :step="1"
            />
          </div>
          <label class="checkbox-field"
            ><input
              type="checkbox"
              :checked="form.time.timeStepSeconds === null"
              @change="
                form.time.timeStepSeconds =
                  form.time.timeStepSeconds === null ? 0.1 : null
              "
            />Автоматичний часовий крок</label
          >
          <NumberField
            v-if="form.time.timeStepSeconds !== null"
            :model-value="form.time.timeStepSeconds"
            @update:model-value="form.time.timeStepSeconds = $event"
            label="Крок часу"
            unit="с"
            :min="0.000001"
          />
        </section>
        <details class="form-section">
          <summary>Початкова концентрація</summary>
          <div class="field-grid">
            <NumberField
              v-model="form.initialCondition.peak"
              label="Пік"
              unit="мг/м³"
              :min="0"
              :max="1000000"
            />
            <NumberField
              v-model="form.initialCondition.sigma"
              label="Розмір σ"
              unit="м"
              :min="0.001"
            />
            <NumberField
              v-model="form.initialCondition.centerX"
              label="Центр X"
              unit="м"
              :min="0.001"
            />
            <NumberField
              v-model="form.initialCondition.centerY"
              label="Центр Y"
              unit="м"
              :min="0.001"
            />
          </div>
        </details>
      </fieldset>
      <p class="field-hint boundary-note">Концентрація на межах: 0.</p>
      <p v-if="error" class="error-box" role="alert">{{ error }}</p>
      <button class="button primary launch-button" :disabled="busy">
        {{ busy ? "Запуск…" : "Запустити симуляцію" }}
      </button>
    </form>
  </section>
</template>
