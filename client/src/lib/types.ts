export type TaskStatus =
  | "Queued"
  | "Running"
  | "Cancelling"
  | "Completed"
  | "Cancelled"
  | "Failed"
  | "TimedOut";
export interface User {
  id: string;
  email: string;
  role: string;
  createdAt: string;
}
export interface Session {
  accessToken: string;
  expiresAt: string;
  user: User;
}
export interface Parameters {
  domain: { lengthX: number; lengthY: number; nodesX: number; nodesY: number };
  transport: { velocityX: number; velocityY: number; diffusion: number };
  time: { durationSeconds: number; timeStepSeconds: number | null };
  initialCondition: {
    peak: number;
    centerX: number;
    centerY: number;
    sigma: number;
  };
  requestedFrames: number;
  boundaryCondition: "ZeroDirichlet";
}
export interface CreateTask extends Parameters {
  name: string;
}
export interface SimulationTask {
  id: string;
  name: string;
  status: TaskStatus;
  progressPercent: number;
  completedSteps: number;
  totalSteps: number;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  version: number;
  attempt: number;
  errorCode: string | null;
  parameters: Parameters;
  estimatedWaitSeconds: number | null;
}
export interface Update {
  taskId: string;
  version: number;
  status: TaskStatus;
  progressPercent: number;
  completedSteps: number;
  totalSteps: number;
  attempt: number;
  errorCode: string | null;
}
export interface TaskPage {
  items: SimulationTask[];
  page: number;
  pageSize: number;
  totalCount: number;
}
export interface Plan {
  dx: number;
  dy: number;
  effectiveTimeStepSeconds: number;
  totalSteps: number;
  estimatedCellUpdates: number;
  frameCount: number;
  estimatedResultBytes: number;
  estimatedWorkingBytes: number;
}
export interface Limits {
  maxActivePerUser: number;
  maxTasksPerUser: number;
  maxActiveSystem: number;
  numerical: {
    maxNodesPerAxis: number;
    maxTimeSteps: number;
    maxFrames: number;
    maxDomainLength: number;
    maxAbsoluteVelocity: number;
    maxDiffusion: number;
    maxDurationSeconds: number;
    maxPeak: number;
  };
}
export interface Frame {
  index: number;
  step: number;
  timeSeconds: number;
  minimumConcentration: number;
  maximumConcentration: number;
}
export interface Result {
  nodesX: number;
  nodesY: number;
  format: string;
  frames: Frame[];
}
export const active = (status: TaskStatus) =>
  ["Queued", "Running", "Cancelling"].includes(status);
export const statusNames: Record<TaskStatus, string> = {
  Queued: "У черзі",
  Running: "Обчислення",
  Cancelling: "Скасування",
  Completed: "Готово",
  Cancelled: "Скасовано",
  Failed: "Помилка",
  TimedOut: "Час вичерпано",
};
export const defaultTask = (): CreateTask => ({
  name: "Перенесення гаусової плями",
  domain: { lengthX: 100, lengthY: 100, nodesX: 101, nodesY: 101 },
  transport: { velocityX: 0.5, velocityY: 0.1, diffusion: 0.1 },
  time: { durationSeconds: 60, timeStepSeconds: null },
  initialCondition: { peak: 1, centerX: 25, centerY: 50, sigma: 5 },
  requestedFrames: 16,
  boundaryCondition: "ZeroDirichlet",
});
export const number = (value: number, digits = 2) =>
  new Intl.NumberFormat("uk-UA", { maximumFractionDigits: digits }).format(
    value,
  );
export const date = (value: string) =>
  new Intl.DateTimeFormat("uk-UA", {
    day: "numeric",
    month: "short",
    hour: "2-digit",
    minute: "2-digit",
  }).format(new Date(value));
