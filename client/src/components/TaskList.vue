<script setup lang="ts">
import { ref, watch } from "vue";
import { active, date, number, statusNames } from "../lib/types";
import { useSimulations } from "../stores/simulations";
const emit = defineEmits<{ select: [id: string] }>();
const jobs = useSimulations();
const deleting = ref<string | null>(null);
const dialog = ref<HTMLDialogElement>();
watch(
  deleting,
  (value) => {
    if (value) dialog.value?.showModal();
  },
  { flush: "post" },
);
async function changePage(delta: number) {
  jobs.page += delta;
  try {
    await jobs.load(true);
  } catch {
    jobs.error = "Не вдалося завантажити історію.";
  }
}
async function changeFilter() {
  jobs.page = 1;
  try {
    await jobs.load(true);
  } catch {
    jobs.error = "Не вдалося завантажити історію.";
  }
}
</script>
<template>
  <section class="panel task-list">
    <div class="panel-heading">
      <h2>
        Історія <span class="muted">({{ jobs.total }})</span>
      </h2>
      <select
        v-model="jobs.filter"
        aria-label="Фільтр стану"
        @change="changeFilter"
      >
        <option value="">Усі стани</option>
        <option
          v-for="(label, status) in statusNames"
          :key="status"
          :value="status"
        >
          {{ label }}
        </option>
      </select>
    </div>
    <p v-if="jobs.loading" class="list-empty">Завантаження…</p>
    <p v-else-if="!jobs.items.length" class="list-empty">
      {{ jobs.filter ? "Задач із цим станом немає." : "Задач немає." }}
    </p>
    <div v-else class="table-scroll">
      <table>
        <thead>
          <tr>
            <th>Симуляція</th>
            <th>Стан</th>
            <th class="hide-mobile">Сітка</th>
            <th>Дії</th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="task in jobs.items"
            :key="task.id"
            :class="{ 'selected-row': jobs.selected?.id === task.id }"
          >
            <td>
              <button class="task-name" @click="emit('select', task.id)">
                {{ task.name }}</button
              ><small>{{ date(task.createdAt) }}</small>
            </td>
            <td>
              <span class="status-badge" :class="task.status.toLowerCase()">{{
                statusNames[task.status]
              }}</span
              ><small v-if="task.status === 'Running'"
                >{{ number(task.progressPercent, 0) }}%</small
              >
            </td>
            <td class="hide-mobile">
              <span class="mono"
                >{{ task.parameters.domain.nodesX }} ×
                {{ task.parameters.domain.nodesY }}</span
              >
            </td>
            <td>
              <button
                v-if="active(task.status)"
                class="text-button"
                aria-label="Скасувати симуляцію"
                :disabled="jobs.busy || task.status === 'Cancelling'"
                @click="jobs.cancel(task.id)"
              >
                Скасувати</button
              ><button
                v-else
                class="text-button danger-text"
                aria-label="Видалити симуляцію"
                :disabled="jobs.busy"
                @click="deleting = task.id"
              >
                Видалити
              </button>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
    <div v-if="jobs.total > 20" class="pagination">
      <span>{{ jobs.page }} / {{ Math.ceil(jobs.total / 20) }}</span>
      <div>
        <button
          class="button secondary"
          aria-label="Попередня сторінка"
          :disabled="jobs.page <= 1 || jobs.loading"
          @click="changePage(-1)"
        >
          ←</button
        ><button
          class="button secondary"
          aria-label="Наступна сторінка"
          :disabled="jobs.page * 20 >= jobs.total || jobs.loading"
          @click="changePage(1)"
        >
          →
        </button>
      </div>
    </div>
    <dialog
      v-if="deleting"
      ref="dialog"
      class="confirm-dialog"
      aria-labelledby="delete-title"
      @cancel.prevent="deleting = null"
      @click.self="deleting = null"
    >
      <h2 id="delete-title">Видалити симуляцію?</h2>
      <p>Задача та її результати будуть видалені.</p>
      <div class="dialog-actions">
        <button class="button secondary" autofocus @click="deleting = null">
          Залишити</button
        ><button
          class="button danger"
          @click="
            jobs.remove(deleting);
            deleting = null;
          "
        >
          Видалити
        </button>
      </div>
    </dialog>
  </section>
</template>
