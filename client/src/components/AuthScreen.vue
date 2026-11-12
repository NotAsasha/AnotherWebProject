<script setup lang="ts">
import { ref } from "vue";
import { Eye, EyeOff } from "@lucide/vue";
import { useAuth } from "../stores/auth";
const auth = useAuth();
const registering = ref(false);
const email = ref("");
const password = ref("");
const visible = ref(false);
const busy = ref(false);
const error = ref("");
async function submit() {
  busy.value = true;
  error.value = "";
  try {
    await auth.login(email.value, password.value, registering.value);
  } catch (failure) {
    error.value =
      failure instanceof Error ? failure.message : "Не вдалося увійти.";
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <main class="auth-shell">
    <section class="auth-box">
      <h1>Міграція домішки</h1>
      <h2>{{ registering ? "Реєстрація" : "Вхід" }}</h2>
      <p v-if="auth.notice" class="notice" role="status">{{ auth.notice }}</p>
      <p v-if="error" class="error-box" role="alert">{{ error }}</p>
      <form @submit.prevent="submit" class="auth-form">
        <label
          >Електронна пошта<input
            v-model="email"
            type="email"
            autocomplete="username"
            maxlength="254"
            required
            :disabled="busy"
        /></label>
        <label
          >Пароль<span class="password-input"
            ><input
              v-model="password"
              :type="visible ? 'text' : 'password'"
              :autocomplete="registering ? 'new-password' : 'current-password'"
              :minlength="registering ? 12 : 1"
              maxlength="128"
              required
              :disabled="busy" /><button
              type="button"
              class="icon-button"
              :aria-label="visible ? 'Приховати пароль' : 'Показати пароль'"
              @click="visible = !visible"
            >
              <EyeOff v-if="visible" :size="18" /><Eye
                v-else
                :size="18"
              /></button></span
        ></label>
        <p v-if="registering" class="field-hint">Пароль: 12–128 символів.</p>
        <button class="button primary" :disabled="busy">
          {{
            busy
              ? "Зачекайте…"
              : registering
                ? "Створити обліковий запис"
                : "Увійти"
          }}
        </button>
      </form>
      <button
        class="text-button auth-switch"
        :disabled="busy"
        @click="
          registering = !registering;
          error = '';
          password = '';
        "
      >
        {{ registering ? "Увійти" : "Зареєструватися" }}
      </button>
    </section>
  </main>
</template>
