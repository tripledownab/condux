import { describe, expect, it } from "vitest";
import { isRefreshedByProjectEvent } from "./project-event-stream";

describe("isRefreshedByProjectEvent", () => {
  it("refreshes the project's queries", () => {
    expect(isRefreshedByProjectEvent(["/api/projects/7/issues"], 7)).toBe(true);
    expect(isRefreshedByProjectEvent(["/api/projects/7/repos/r1/cve-fixes"], 7)).toBe(true);
  });

  it("leaves the CVE findings alone, which no project event can change", () => {
    expect(isRefreshedByProjectEvent(["/api/projects/7/repos/r1/cve-findings"], 7)).toBe(false);
  });

  it("leaves other projects' queries and non-string keys alone", () => {
    expect(isRefreshedByProjectEvent(["/api/projects/8/issues"], 7)).toBe(false);
    expect(isRefreshedByProjectEvent(["/api/projects/70/issues"], 7)).toBe(false);
    expect(isRefreshedByProjectEvent([42], 7)).toBe(false);
  });
});
