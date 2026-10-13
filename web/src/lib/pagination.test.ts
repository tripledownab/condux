import { describe, expect, it } from "vitest";
import { toPageForSize, toPageItems } from "./pagination";

describe("toPageItems", () => {
  it("shows every page when there are few", () => {
    expect(toPageItems(0, 1)).toEqual([0]);
    expect(toPageItems(3, 7)).toEqual([0, 1, 2, 3, 4, 5, 6]);
  });

  it("shows nothing for an empty list", () => {
    expect(toPageItems(0, 0)).toEqual([]);
  });

  it("starts windowing at the first count that does not fit", () => {
    expect(toPageItems(0, 8)).toEqual([0, 1, 2, 3, 4, "gap", 7]);
  });

  it("keeps the first pages together near the start", () => {
    expect(toPageItems(0, 20)).toEqual([0, 1, 2, 3, 4, "gap", 19]);
    expect(toPageItems(3, 20)).toEqual([0, 1, 2, 3, 4, "gap", 19]);
  });

  it("keeps the last pages together near the end", () => {
    expect(toPageItems(19, 20)).toEqual([0, "gap", 15, 16, 17, 18, 19]);
    expect(toPageItems(16, 20)).toEqual([0, "gap", 15, 16, 17, 18, 19]);
  });

  it("shows the current page with one neighbour each side in the middle", () => {
    expect(toPageItems(4, 20)).toEqual([0, "gap", 3, 4, 5, "gap", 19]);
    expect(toPageItems(10, 20)).toEqual([0, "gap", 9, 10, 11, "gap", 19]);
    expect(toPageItems(15, 20)).toEqual([0, "gap", 14, 15, 16, "gap", 19]);
  });

  it("keeps the same width wherever the current page is", () => {
    for (let page = 0; page < 30; page++) {
      expect(toPageItems(page, 30)).toHaveLength(7);
    }
  });

  it("always includes the current page, the first and the last, with no gap hiding a single page", () => {
    for (const pageCount of [8, 9, 12, 30]) {
      for (let page = 0; page < pageCount; page++) {
        const items = toPageItems(page, pageCount);
        expect(items).toContain(page);
        expect(items[0]).toBe(0);
        expect(items.at(-1)).toBe(pageCount - 1);
        // A gap must stand for at least two pages, or showing the number would have cost no more.
        items.forEach((item, i) => {
          if (item === "gap") {
            expect((items[i + 1] as number) - (items[i - 1] as number)).toBeGreaterThan(2);
          }
        });
      }
    }
  });

  it("clamps a page outside the range", () => {
    expect(toPageItems(-3, 20)).toEqual(toPageItems(0, 20));
    expect(toPageItems(99, 20)).toEqual(toPageItems(19, 20));
  });
});

describe("toPageForSize", () => {
  it("keeps the first row of the current page in view", () => {
    // Rows 26-50 at 25 a page; at 10 a page, row 26 is on the third page (21-30).
    expect(toPageForSize(1, 25, 10)).toBe(2);
    // Rows 21-30 at 10 a page; at 25 a page, row 21 is on the first page (1-25).
    expect(toPageForSize(2, 10, 25)).toBe(0);
    // Rows 51-75 at 25 a page; at 50 a page, row 51 starts the second page.
    expect(toPageForSize(2, 25, 50)).toBe(1);
  });

  it("stays on the first page from the first page", () => {
    expect(toPageForSize(0, 25, 10)).toBe(0);
    expect(toPageForSize(0, 10, 50)).toBe(0);
  });
});
