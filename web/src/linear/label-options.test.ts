import { describe, expect, it } from "vitest";
import { toLabelOptions } from "./label-options";

const inGroup = (group: string, label: string) => `${group} / ${label}`;

describe("toLabelOptions", () => {
  it("names a grouped label with its group and sorts so each group's labels sit together", () => {
    const options = toLabelOptions(
      [
        { id: "ui", name: "ui", groupId: "area", groupName: "Area" },
        { id: "bug", name: "Bug", groupId: null, groupName: null },
        { id: "be", name: "backend", groupId: "area", groupName: "Area" },
        { id: "chore", name: "chore", groupId: null, groupName: null },
        { id: "api", name: "api", groupId: null, groupName: null },
      ],
      [],
      inGroup,
    );

    expect(options).toEqual([
      { value: "api", label: "api" },
      { value: "be", label: "Area / backend" },
      { value: "ui", label: "Area / ui" },
      { value: "bug", label: "Bug" },
      { value: "chore", label: "chore" },
    ]);
  });

  it("leaves out the labels already chosen and the siblings of a group already used", () => {
    const options = toLabelOptions(
      [
        { id: "be", name: "backend", groupId: "area", groupName: "Area" },
        { id: "ui", name: "ui", groupId: "area", groupName: "Area" },
        { id: "bug", name: "Bug", groupId: null, groupName: null },
        { id: "chore", name: "chore", groupId: null, groupName: null },
      ],
      ["be", "bug"],
      inGroup,
    );

    expect(options).toEqual([{ value: "chore", label: "chore" }]);
  });
});
