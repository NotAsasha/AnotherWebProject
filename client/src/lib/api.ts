const sessionKey = "contaminant.session";
export const readSession = (): import("./types").Session | null => {
  try {
    const value = JSON.parse(sessionStorage.getItem(sessionKey) ?? "null");
    if (
      value?.accessToken &&
      value?.user?.id &&
      Date.parse(value.expiresAt) > Date.now()
    )
      return value;
    sessionStorage.removeItem(sessionKey);
    return null;
  } catch {
    sessionStorage.removeItem(sessionKey);
    return null;
  }
};
let token = readSession()?.accessToken ?? "";
export function saveSession(session: import("./types").Session | null) {
  token = session?.accessToken ?? "";
  if (session) sessionStorage.setItem(sessionKey, JSON.stringify(session));
  else sessionStorage.removeItem(sessionKey);
}
export const accessToken = () => token;
export class ApiError extends Error {
  status: number;
  fields: Record<string, string[]>;
  constructor(
    status: number,
    message: string,
    fields: Record<string, string[]> = {},
  ) {
    super(message);
    this.status = status;
    this.fields = fields;
  }
}
export async function response(
  path: string,
  options: RequestInit = {},
): Promise<Response> {
  const headers = new Headers(options.headers);
  if (token) headers.set("Authorization", `Bearer ${token}`);
  if (options.body) headers.set("Content-Type", "application/json");
  let result: Response;
  try {
    result = await fetch(`/api/v1/${path}`, { ...options, headers });
  } catch (error) {
    if (error instanceof Error && error.name === "AbortError") throw error;
    throw new ApiError(
      0,
      "Не вдалося зв’язатися із сервером. Перевірте з’єднання та повторіть спробу.",
    );
  }
  if (!result.ok) {
    const problem = await result.json().catch(() => ({}));
    if (result.status === 401 && token && !path.startsWith("auth/login"))
      window.dispatchEvent(new Event("session-expired"));
    let message =
      (
        {
          400: "Перевірте введені параметри.",
          401: "Потрібно увійти знову.",
          403: "Недостатньо прав.",
          404: "Задачу не знайдено або її вже видалено.",
          409: "Стан змінився. Оновіть дані й повторіть дію.",
          429: "Досягнуто ліміту. Завершіть активні задачі або спробуйте пізніше.",
          502: "Сервіс тимчасово недоступний. Повторіть спробу.",
          503: "Сервіс готується до роботи. Спробуйте ще раз.",
        } as Record<number, string>
      )[result.status] ?? "Сталася помилка сервера. Повторіть спробу.";
    if (problem.code === "InvalidCredentials")
      message = "Неправильна електронна пошта або пароль.";
    if (problem.code === "EmailAlreadyRegistered")
      message = "Ця електронна пошта вже зареєстрована.";
    if (problem.title === "Task quota exceeded.")
      message =
        "Ліміт задач вичерпано. Скасуйте активну задачу або видаліть завершену з історії.";
    if (path === "auth/register" && result.status === 400)
      message = "Перевірте пошту та пароль: від 12 до 128 символів.";
    throw new ApiError(result.status, message, problem.errors ?? {});
  }
  return result;
}
export async function api<T>(path: string, options?: RequestInit): Promise<T> {
  const result = await response(path, options);
  return result.status === 204 ? (undefined as T) : result.json();
}
