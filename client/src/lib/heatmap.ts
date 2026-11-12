const stops = [
  [68, 1, 84],
  [59, 82, 139],
  [33, 145, 140],
  [94, 201, 98],
  [253, 231, 37],
] as const;
export function decodeFrame(
  buffer: ArrayBuffer,
  nx: number,
  ny: number,
): Float32Array {
  if (
    !Number.isInteger(nx) ||
    !Number.isInteger(ny) ||
    nx < 3 ||
    ny < 3 ||
    nx > 512 ||
    ny > 512 ||
    buffer.byteLength !== nx * ny * 4
  )
    throw new Error("Розмір кадру не відповідає сітці.");
  const view = new DataView(buffer);
  const values = new Float32Array(nx * ny);
  for (let i = 0; i < values.length; i++) {
    const value = view.getFloat32(i * 4, true);
    if (!Number.isFinite(value))
      throw new Error("Кадр містить некоректні значення.");
    values[i] = value;
  }
  return values;
}
export function pixels(
  values: Float32Array,
  nx: number,
  ny: number,
  maximum: number,
): Uint8ClampedArray<ArrayBuffer> {
  const result = new Uint8ClampedArray(nx * ny * 4);
  const scale = maximum > 0 ? maximum : 1;
  for (let y = 0; y < ny; y++)
    for (let x = 0; x < nx; x++) {
      const value =
        Math.max(0, Math.min(1, values[y * nx + x]! / scale)) *
        (stops.length - 1);
      const lower = Math.min(stops.length - 2, Math.floor(value));
      const fraction = value - lower;
      const offset = ((ny - 1 - y) * nx + x) * 4;
      for (let c = 0; c < 3; c++)
        result[offset + c] = Math.round(
          stops[lower]![c]! * (1 - fraction) + stops[lower + 1]![c]! * fraction,
        );
      result[offset + 3] = 255;
    }
  return result;
}
