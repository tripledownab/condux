// The labels the picker offers a project: what Linear lists, less what the project already carries and the
// siblings of a group it already uses, named with their group and sorted so each group's labels sit together.

import type { LinearLabelResponse } from "@/src/api/generated/model";
import type { ComboboxOption } from "@/src/components/ui/combobox";

// `nameInGroup` words a grouped label, so the separator comes from the messages rather than from here.
export function toLabelOptions(
  offered: readonly LinearLabelResponse[],
  chosen: readonly string[],
  nameInGroup: (group: string, label: string) => string,
): ComboboxOption[] {
  // A group lets an issue carry one of its labels, so a group the project already uses offers no more.
  const usedGroups = new Set(
    offered
      .filter((label) => chosen.includes(label.id) && label.groupId)
      .map((label) => label.groupId),
  );
  return offered
    .filter(
      (label) => !chosen.includes(label.id) && !(label.groupId && usedGroups.has(label.groupId)),
    )
    .map((label) => ({
      value: label.id,
      label: label.groupName ? nameInGroup(label.groupName, label.name) : label.name,
    }))
    .sort((a, b) => a.label.localeCompare(b.label, undefined, { sensitivity: "base" }));
}
