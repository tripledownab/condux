import { afterEach, describe, expect, it, vi } from "vitest";
import { ConduxApiError, conduxFetch } from "./fetcher";

// Stub global fetch with a single canned Response.
const stubFetch = (body: BodyInit | null, init: ResponseInit) =>
  vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(body, init));

afterEach(() => vi.restoreAllMocks());

describe("conduxFetch", () => {
  it("wraps a 2xx JSON body as { status, data, headers } (orval's fetch shape)", async () => {
    stubFetch(JSON.stringify({ id: 1, title: "boom" }), {
      status: 200,
      headers: { "content-type": "application/json" },
    });

    const res = await conduxFetch<{ status: number; data: { id: number; title: string } }>(
      "/api/projects/1/issues",
    );

    expect(res.status).toBe(200);
    expect(res.data).toEqual({ id: 1, title: "boom" });
  });

  it("returns undefined data for 204 No Content (e.g. key revoke)", async () => {
    stubFetch(null, { status: 204 });

    const res = await conduxFetch<{ status: number; data: unknown }>(
      "/api/projects/1/keys/2/revoke",
      {
        method: "POST",
      },
    );

    expect(res.status).toBe(204);
    expect(res.data).toBeUndefined();
  });

  it("throws a ConduxApiError carrying the status and error code on a non-2xx response", async () => {
    stubFetch(JSON.stringify({ error: "email_taken" }), { status: 409 });

    const error = await conduxFetch("/api/auth/signup", { method: "POST" }).catch((e) => e);

    expect(error).toBeInstanceOf(ConduxApiError);
    const apiError = error as ConduxApiError;
    expect(apiError.status).toBe(409);
    expect(apiError.code).toBe("email_taken");
  });

  it("sends the session cookie by passing credentials: include", async () => {
    const spy = stubFetch(JSON.stringify({ id: 1, email: "a@b.co" }), { status: 200 });

    await conduxFetch("/api/auth/me");

    expect(spy).toHaveBeenCalledWith(
      expect.stringContaining("/api/auth/me"),
      expect.objectContaining({ credentials: "include" }),
    );
  });
});
