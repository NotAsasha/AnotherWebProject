<script setup lang="ts">
defineProps<{
  modelValue: number;
  label: string;
  unit?: string;
  min?: number;
  max?: number;
  step?: string | number;
}>();
const emit = defineEmits<{ "update:modelValue": [value: number] }>();
function update(event: Event) {
  emit("update:modelValue", (event.target as HTMLInputElement).valueAsNumber);
}
</script>
<template>
  <label class="number-field"
    ><span
      >{{ label }}<small v-if="unit">{{ unit }}</small></span
    ><input
      type="number"
      :value="modelValue"
      @input="update"
      :min="min"
      :max="max"
      :step="step ?? 'any'"
      required
      :aria-label="label"
  /></label>
</template>
