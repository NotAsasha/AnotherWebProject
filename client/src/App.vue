<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from "vue";
import AuthScreen from "./components/AuthScreen.vue";
import ParameterForm from "./components/ParameterForm.vue";
import TaskList from "./components/TaskList.vue";
import ResultViewer from "./components/ResultViewer.vue";
import { useAuth } from "./stores/auth";
import { useSimulations } from "./stores/simulations";
import { active } from "./lib/types";
const auth = useAuth();
const jobs = useSimulations();
const tab = ref<"workspace" | "history">("workspace");
const form = ref<InstanceType<typeof ParameterForm>>();
let transition = 0;
watch(
  () => auth.user?.id,
  async (id) => {
    const epoch = ++transition;
    await jobs.stop();
    if (epoch === transition && id && id === auth.user?.id) await jobs.start();
  },
  { immediate: true },
);
onMounted(async () => {
  try {
    await auth.restore();
  } catch {
    jobs.error = "Не вдалося перевірити сесію. Спробуйте оновити сторінку.";
  }
});
function workspace() {
  tab.value = "workspace";
  if (jobs.page !== 1 || jobs.filter) {
    jobs.page = 1;
    jobs.filter = "";
    void jobs.refresh();
  }
}
async function select(id: string) {
  await jobs.select(id);
  workspace();
  scrollOnMobile(".result-panel");
}
function scrollOnMobile(selector: string) {
  if (window.matchMedia("(max-width:720px)").matches)
    setTimeout(
      () =>
        document.querySelector(selector)?.scrollIntoView({
          behavior: window.matchMedia("(prefers-reduced-motion:reduce)").matches
            ? "instant"
            : "smooth",
          block: "start",
        }),
      0,
    );
}
function reuse() {
  if (jobs.selected) {
    form.value?.reuse(jobs.selected);
    scrollOnMobile(".parameter-panel");
  }
}
onBeforeUnmount(() => {
  void jobs.stop();
});
</script>

<template>
  <AuthScreen v-if="!auth.user" />
  <div v-else>
    <a href="#workspace" class="skip-link">Перейти до вмісту</a>
    <header class="topbar" :data-connection="jobs.connection">
      <h1>Міграція домішки</h1>
      <nav aria-label="Основна навігація">
        <button
          :class="{ selected: tab === 'workspace' }"
          aria-label="Лабораторія"
          :aria-current="tab === 'workspace' ? 'page' : undefined"
          @click="workspace"
        >
          Симуляція
        </button>
        <button
          :class="{ selected: tab === 'history' }"
          :aria-current="tab === 'history' ? 'page' : undefined"
          @click="tab = 'history'"
        >
          Історія
        </button>
      </nav>
      <span class="account-email">{{ auth.user.email }}</span>
      <button
        class="button secondary"
        aria-label="Вийти з облікового запису"
        @click="auth.logout()"
      >
        Вийти
      </button>
    </header>
    <main id="workspace" class="workspace">
      <div v-if="jobs.error" class="error-box global-error" role="alert">
        <span>{{ jobs.error }}</span>
        <button class="text-button" @click="jobs.refresh">Повторити</button>
        <button
          class="icon-button"
          aria-label="Закрити повідомлення"
          @click="jobs.error = ''"
        >
          ×
        </button>
      </div>
      <div v-show="tab === 'workspace'" class="lab-grid">
        <ParameterForm ref="form" @created="scrollOnMobile('.result-panel')" />
        <div class="results-column">
          <div v-if="jobs.selected" class="experiment-strip">
            <h2>{{ jobs.selected.name }}</h2>
            <div class="row-actions">
              <button class="text-button" @click="reuse">
                Використати параметри
              </button>
              <button
                v-if="active(jobs.selected.status)"
                class="button secondary"
                :disabled="jobs.busy || jobs.selected.status === 'Cancelling'"
                @click="jobs.cancel(jobs.selected.id)"
              >
                Скасувати
              </button>
            </div>
          </div>
          <ResultViewer :task="jobs.selected" />
        </div>
      </div>
      <TaskList v-if="tab === 'history'" @select="select" />
    </main>
  </div>
</template>
