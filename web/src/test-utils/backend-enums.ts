import { readFileSync } from "node:fs";
import { resolve } from "node:path";

// Holds a TypeScript copy of a backend enum to the C# original: the API sends these as bare integers, so
// the two are kept in step only by a test that reads both.

// A TypeScript enum's numeric members, by name.
export function toNumericMembers(values: object): Record<string, number> {
  return Object.fromEntries(
    Object.entries(values).filter(([, value]) => typeof value === "number"),
  );
}

// An enum in backend/src/Condux.Core/<file>, by name. Strict on purpose: every member must read as
// `Name = number`, so one this parse cannot read (an attribute, an implicit value) fails the test
// instead of being skipped, which would pass on a member missing here. Comments go first, since they
// may hold commas and braces. minimum is how many members the enum is known to have at least, so a
// parse that read fewer fails.
export function readBackendEnum(
  file: string,
  name: string,
  minimum: number,
): Record<string, number> {
  // Vitest runs from web/, under jsdom, where import.meta.url is not a file URL.
  const backend = readFileSync(resolve(process.cwd(), "../backend/src/Condux.Core", file), "utf8");
  const code = backend.replace(/\/\*[\s\S]*?\*\//g, "").replace(/\/\/.*$/gm, "");
  const body = new RegExp(`public enum ${name}\\s*\\{([^}]*)\\}`).exec(code)?.[1] ?? "";
  const members = body
    .split(",")
    .map((member) => member.trim())
    .filter((member) => member !== "");
  const declared = Object.fromEntries(
    members.map((member) => {
      const parsed = /^(\w+)\s*=\s*(\d+)$/.exec(member);
      if (!parsed) {
        throw new Error(`${file}: cannot read enum member "${member}"`);
      }
      return [parsed[1], Number(parsed[2])];
    }),
  );
  // A parse that found nothing would compare two empty sets, so prove it read the enum.
  if (Object.keys(declared).length < minimum) {
    throw new Error(`${file}: read only ${Object.keys(declared).length} members of ${name}`);
  }
  return declared;
}
