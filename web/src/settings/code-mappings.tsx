"use client";

// A linked repository's code mappings: the stackRoot to sourceRoot rules that turn a stack frame's path
// into a repo path, managed by hand or added from the suggestions derived from recent errors.

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import {
  getListCodeMappingsQueryKey,
  useAddCodeMapping,
  useDeleteCodeMapping,
  useListCodeMappings,
  useSuggestedMappings,
} from "@/src/api/generated/condux";
import type { CodeMapping, DerivedMapping } from "@/src/api/generated/model";
import { FIELD_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { lookUpCodeKey } from "@/src/lib/api-errors";

// Why a suggestion request offered nothing, one message per outcome the server names. An outcome this
// build does not know reads as the server being unavailable, which is the answer that asks for a retry.
const EMPTY_MESSAGE_KEYS: Record<string, string> = {
  not_connected: "suggestNotConnected",
  no_frames: "suggestNoFrames",
  no_match: "suggestNoMatch",
  ambiguous: "suggestAmbiguous",
  already_mapped: "suggestAlreadyMapped",
  conflicting: "suggestConflicting",
  unavailable: "suggestUnavailable",
};

function toEmptyMessageKey(outcome: string) {
  return lookUpCodeKey(EMPTY_MESSAGE_KEYS, outcome) ?? "suggestUnavailable";
}

export function CodeMappings({
  projectId,
  repoId,
  canManage,
}: {
  projectId: number;
  repoId: string;
  canManage: boolean;
}) {
  const translate = useTranslations("settings.repos");
  const queryClient = useQueryClient();
  const mappings = useListCodeMappings(projectId, repoId);
  const addMapping = useAddCodeMapping();
  const deleteMapping = useDeleteCodeMapping();
  // On-demand: only fetches when the user asks (the Suggest button calls refetch), since it hits GitHub.
  const suggestions = useSuggestedMappings(projectId, repoId, { query: { enabled: false } });
  const [stackRoot, setStackRoot] = useState("");
  const [sourceRoot, setSourceRoot] = useState("");

  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: getListCodeMappingsQueryKey(projectId, repoId) });

  const add = () => {
    const stack = stackRoot.trim();
    if (stack === "") {
      return;
    }
    addMapping.mutate(
      { projectId, repoId, data: { stackRoot: stack, sourceRoot: sourceRoot.trim() || null } },
      {
        onSuccess: () => {
          setStackRoot("");
          setSourceRoot("");
          invalidate();
        },
      },
    );
  };

  const remove = (mappingId: string) =>
    deleteMapping.mutate({ projectId, repoId, mappingId }, { onSuccess: invalidate });

  const addSuggestion = (suggestion: DerivedMapping) =>
    addMapping.mutate(
      {
        projectId,
        repoId,
        data: { stackRoot: suggestion.stackRoot, sourceRoot: suggestion.sourceRoot },
      },
      {
        onSuccess: () => {
          invalidate();
          suggestions.refetch(); // the added one drops off the suggestion list
        },
      },
    );

  const list: CodeMapping[] = mappings.data?.data ?? [];
  const answer = suggestions.data?.data;
  const suggested: DerivedMapping[] = answer?.suggestions ?? [];
  return (
    <div className="mt-3 border-t border-border pt-3">
      <span className="text-xs uppercase text-muted-foreground">{translate("mappingsTitle")}</span>
      {list.length === 0 ? (
        <p className="mt-1 text-xs text-muted-foreground">{translate("mappingsEmpty")}</p>
      ) : (
        <ul className="mt-2 flex flex-col gap-1">
          {list.map((mapping) => (
            <li key={mapping.id} className="flex items-center justify-between gap-2 text-xs">
              <code className="text-foreground">
                {mapping.stackRoot || translate("anyPath")} →{" "}
                {mapping.sourceRoot || translate("repoRoot")}
              </code>
              {canManage ? (
                <button
                  type="button"
                  onClick={() => remove(mapping.id)}
                  disabled={deleteMapping.isPending}
                  className="text-muted-foreground hover:text-error"
                >
                  {translate("delete")}
                </button>
              ) : null}
            </li>
          ))}
        </ul>
      )}
      {canManage ? (
        <>
          <div className="mt-2 flex flex-wrap items-center gap-2">
            <input
              aria-label={translate("stackRootLabel")}
              value={stackRoot}
              onChange={(event) => setStackRoot(event.target.value)}
              placeholder={translate("stackRootPlaceholder")}
              className={`${FIELD_CLASS} w-40 text-xs`}
            />
            <input
              aria-label={translate("sourceRootLabel")}
              value={sourceRoot}
              onChange={(event) => setSourceRoot(event.target.value)}
              placeholder={translate("sourceRootPlaceholder")}
              className={`${FIELD_CLASS} w-32 text-xs`}
            />
            <button
              type="button"
              onClick={add}
              disabled={addMapping.isPending}
              className={SECONDARY_BUTTON_CLASS}
            >
              {translate("addMapping")}
            </button>
            <button
              type="button"
              onClick={() => suggestions.refetch()}
              disabled={suggestions.isFetching}
              className="text-xs text-primary hover:underline disabled:opacity-50"
            >
              {suggestions.isFetching ? translate("suggesting") : translate("suggestMappings")}
            </button>
          </div>

          {answer !== undefined && suggested.length === 0 ? (
            <p className="mt-2 text-xs text-muted-foreground">
              {translate(toEmptyMessageKey(answer.outcome))}
            </p>
          ) : null}
          {suggested.length > 0 ? (
            <ul className="mt-2 flex flex-col gap-1">
              {suggested.map((suggestion) => (
                <li
                  key={`${suggestion.stackRoot}->${suggestion.sourceRoot}`}
                  className="flex items-center justify-between gap-2 text-xs"
                >
                  <code className="text-muted-foreground">
                    {suggestion.stackRoot || translate("anyPath")} →{" "}
                    {suggestion.sourceRoot || translate("repoRoot")}
                  </code>
                  <span className="flex items-center gap-2">
                    <span className="text-muted-foreground">
                      {translate("suggestMatches", { count: suggestion.matchCount })}
                    </span>
                    <button
                      type="button"
                      onClick={() => addSuggestion(suggestion)}
                      disabled={addMapping.isPending}
                      className="text-primary hover:underline"
                    >
                      {translate("add")}
                    </button>
                  </span>
                </li>
              ))}
            </ul>
          ) : null}
        </>
      ) : null}
    </div>
  );
}
