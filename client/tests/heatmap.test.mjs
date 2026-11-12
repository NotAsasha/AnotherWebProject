import test from "node:test";
import assert from "node:assert/strict";
import { decodeFrame, pixels } from "../src/lib/heatmap.ts";

test("Float32 data uses little endian, including fractional values", () => {
  const buffer = new ArrayBuffer(36);
  const view = new DataView(buffer);
  view.setFloat32(0, 1, true);
  view.setFloat32(4, 0.125, true);
  view.setFloat32(32, 2.5, true);
  assert.deepEqual(
    [...decodeFrame(buffer, 3, 3)],
    [1, 0.125, 0, 0, 0, 0, 0, 0, 2.5],
  );
});
test("Truncated data and non-finite concentrations are rejected", () => {
  assert.throws(() => decodeFrame(new ArrayBuffer(32), 3, 3));
  const buffer = new ArrayBuffer(36);
  new DataView(buffer).setFloat32(0, NaN, true);
  assert.throws(() => decodeFrame(buffer, 3, 3));
});
test("Canvas top row represents maximum Y, not the first solver row", () => {
  const data = new Float32Array([0, 0, 0, 0, 0.5, 0, 1, 0, 0]);
  const rgba = pixels(data, 3, 3, 1);
  assert.deepEqual([...rgba.slice(0, 4)], [253, 231, 37, 255]);
  assert.deepEqual([...rgba.slice(24, 28)], [68, 1, 84, 255]);
  assert.deepEqual([...rgba.slice(16, 20)], [33, 145, 140, 255]);
});
test("Zero fields and fixed global scale remain finite and consistent between frames", () => {
  const zero = pixels(new Float32Array(9), 3, 3, 0);
  assert.deepEqual([...zero.slice(0, 4)], [68, 1, 84, 255]);
  const half = pixels(new Float32Array(9).fill(0.5), 3, 3, 1);
  assert.deepEqual([...half.slice(0, 4)], [33, 145, 140, 255]);
});
