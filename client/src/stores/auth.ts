import { computed, ref } from "vue";
import { defineStore } from "pinia";
import { api, readSession, saveSession } from "../lib/api";
import type { Session, User } from "../lib/types";

export const useAuth = defineStore("auth", () => {
  const session = ref<Session | null>(readSession());
  const user = computed(() => session.value?.user ?? null);
  const notice = ref("");
  let expiration: ReturnType<typeof setTimeout> | undefined;
  function armExpiration() {
    clearTimeout(expiration);
    if (session.value)
      expiration = setTimeout(
        () => logout("Сесію завершено. Увійдіть знову."),
        Math.max(0, Date.parse(session.value.expiresAt) - Date.now()),
      );
  }
  function logout(message = "") {
    clearTimeout(expiration);
    saveSession(null);
    session.value = null;
    notice.value = message;
    sessionStorage.removeItem("contaminant.pending");
    sessionStorage.removeItem("contaminant.selected");
  }
  async function login(email: string, password: string, register: boolean) {
    const credentials = JSON.stringify({ email: email.trim(), password });
    if (register)
      await api("auth/register", { method: "POST", body: credentials });
    const value = await api<Session>("auth/login", {
      method: "POST",
      body: credentials,
    });
    saveSession(value);
    session.value = value;
    notice.value = "";
    armExpiration();
  }
  async function restore() {
    if (!session.value) return;
    try {
      session.value.user = await api<User>("auth/me");
    } catch (error) {
      if (!session.value) return;
      throw error;
    }
  }
  window.addEventListener("session-expired", () =>
    logout("Сесію завершено. Увійдіть знову."),
  );
  armExpiration();
  return { session, user, notice, login, logout, restore };
});
