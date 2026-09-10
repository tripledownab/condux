"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { type FormEvent, useState } from "react";
import {
  getListIssueNotesQueryKey,
  useCreateIssueNote,
  useDeleteIssueNote,
  useListIssueNotes,
} from "@/src/api/generated/condux";
import type { NoteResponse } from "@/src/api/generated/model";
import { Button } from "@/src/components/ui/button";
import { formatRelativeTime } from "@/src/lib/time";

// The notes rail panel on the issue detail: any org member reads and adds free-form notes; the note's
// author or an admin can delete one (the backend enforces both, this only gates the button's visibility).
export function IssueNotes({
  projectId,
  issueId,
  currentUserId,
  canModerate,
}: {
  projectId: number;
  issueId: string;
  currentUserId: number | null;
  canModerate: boolean;
}) {
  const translate = useTranslations("issues.notes");
  const queryClient = useQueryClient();
  const [body, setBody] = useState("");

  const notes = useListIssueNotes(projectId, issueId);
  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: getListIssueNotesQueryKey(projectId, issueId) });
  const create = useCreateIssueNote({
    mutation: {
      onSuccess: () => {
        setBody("");
        invalidate();
      },
    },
  });
  const remove = useDeleteIssueNote({ mutation: { onSuccess: invalidate } });

  const list: NoteResponse[] = notes.data?.data ?? [];
  // The currentUserId guard is load-bearing: a note written over MCP has a null author, so without it a
  // signed-out or still-loading viewer would match null to null, see a Delete button, and get a 403.
  const canDelete = (note: NoteResponse) =>
    canModerate || (currentUserId !== null && note.authorUserId === currentUserId);
  // An agent's note names the MCP token that wrote it, marked so it does not read as a colleague.
  const authorOf = (note: NoteResponse) =>
    note.authorEmail ??
    (note.authorTokenName === null
      ? translate("unknownAuthor")
      : translate("agentAuthor", { name: note.authorTokenName }));

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const trimmed = body.trim();
    if (trimmed !== "") {
      create.mutate({ projectId, issueId, data: { body: trimmed } });
    }
  };

  return (
    <section>
      <h2 className="text-xs font-medium uppercase text-muted-foreground">{translate("title")}</h2>

      <ul className="mt-2 flex flex-col gap-2">
        {list.map((note) => (
          <li key={note.id} className="rounded-md border border-border bg-card p-2.5 text-sm">
            <p className="whitespace-pre-wrap break-words text-foreground">{note.body}</p>
            <div className="mt-1.5 flex items-center justify-between gap-2 text-xs text-muted-foreground">
              <span className="min-w-0 truncate">
                {authorOf(note)} · {formatRelativeTime(note.createdAt)}
              </span>
              {canDelete(note) ? (
                <button
                  type="button"
                  onClick={() => remove.mutate({ projectId, issueId, noteId: note.id })}
                  disabled={remove.isPending}
                  className="shrink-0 text-muted-foreground transition-colors hover:text-error"
                >
                  {translate("delete")}
                </button>
              ) : null}
            </div>
          </li>
        ))}
      </ul>
      {list.length === 0 && !notes.isPending ? (
        <p className="mt-2 text-xs text-muted-foreground">{translate("empty")}</p>
      ) : null}

      <form onSubmit={submit} className="mt-2 flex flex-col gap-2">
        <textarea
          value={body}
          onChange={(event) => setBody(event.target.value)}
          placeholder={translate("placeholder")}
          rows={2}
          className="w-full rounded-md border border-border bg-card px-2 py-1.5 text-sm text-foreground"
        />
        {create.isError ? (
          <p role="alert" className="text-xs text-error">
            {translate("error")}
          </p>
        ) : null}
        <Button
          type="submit"
          variant="secondary"
          size="sm"
          disabled={create.isPending || body.trim() === ""}
        >
          {translate("add")}
        </Button>
      </form>
    </section>
  );
}
