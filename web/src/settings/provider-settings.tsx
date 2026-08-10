"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useFormatter, useTranslations } from "next-intl";
import { type FormEvent, useState } from "react";
import type { ConduxApiError } from "@/src/api/fetcher";
import {
  getGetLlmConfigQueryKey,
  useDeleteLlmConfig,
  useGetLlmConfig,
  useListLlmModels,
  useSetLlmConfig,
} from "@/src/api/generated/condux";
import type { LlmModel } from "@/src/api/generated/model";
import {
  FIELD_CLASS,
  Field,
  PRIMARY_BUTTON_CLASS,
  SECONDARY_BUTTON_CLASS,
} from "@/src/components/form";
import { Notice } from "@/src/components/notice";
import { Combobox } from "@/src/components/ui/combobox";
import { OrgStatus, useCurrentOrg } from "@/src/orgs/current-org";

const DEFAULT_MODEL = "claude-opus-4-8";
// The BYO providers, matching the backend LlmProviders. OpenAI-compatible covers OpenAI, Azure OpenAI,
// vLLM, Ollama and LiteLLM behind one base URL.
const PROVIDERS = ["anthropic", "openai-compat"] as const;

// Map a save failure to a message key: the plan gate (409), the feature being off on this deployment
// (404 — no secret store), a rejected key (400), a role gap (403), else generic.
function saveErrorKey(error: unknown): string {
  const status = (error as ConduxApiError | null)?.status;
  if (status === 409) {
    return "requiresUpgrade";
  }
  if (status === 404) {
    return "notConfigured";
  }
  if (status === 400) {
    return "invalidKey";
  }
  if (status === 403) {
    return "adminOnly";
  }
  return "failed";
}

// The AI provider settings tab (#65, BYO-key): an org runs the Conductor on its own API key instead of
// the platform key. The key is validated and encrypted server-side and never read back, so this shows
// only the provider + model, and re-entering the key replaces it. Managing is admin+.
export function ProviderSettings() {
  const translate = useTranslations("settings.provider");
  const tCommon = useTranslations("common");
  const format = useFormatter();
  const queryClient = useQueryClient();
  const current = useCurrentOrg();
  const orgReady = current.status === OrgStatus.Ready;
  const orgId = orgReady ? current.org.id : 0;

  const config = useGetLlmConfig(orgId, { query: { enabled: orgReady } });
  const save = useSetLlmConfig();
  const remove = useDeleteLlmConfig();
  const listModels = useListLlmModels();

  const [provider, setProvider] = useState<string>("anthropic");
  const [baseUrl, setBaseUrl] = useState("");
  const [model, setModel] = useState(DEFAULT_MODEL);
  const [apiKey, setApiKey] = useState("");
  // The live model list from the provider; empty until loaded (then the model field is a picker).
  const [models, setModels] = useState<LlmModel[]>([]);

  const needsBaseUrl = provider === "openai-compat";

  // Fetch the models the key can use — the entered key when adding/replacing one, otherwise the org's
  // stored key (the server decrypts it; the key never comes back to the browser).
  const loadModels = () =>
    listModels.mutate(
      { orgId, data: { provider, baseUrl: baseUrl.trim() || null, apiKey: apiKey.trim() || null } },
      {
        onSuccess: (response) => {
          if (response.status !== 200) {
            return;
          }
          setModels(response.data.models);
          if (
            response.data.models.length > 0 &&
            !response.data.models.some((m) => m.id === model)
          ) {
            setModel(response.data.models[0].id);
          }
        },
      },
    );

  if (current.status === OrgStatus.Loading) {
    return <Notice>{translate("loading")}</Notice>;
  }
  if (current.status === OrgStatus.Error) {
    return <Notice>{translate("error")}</Notice>;
  }
  if (current.status === OrgStatus.NoOrg) {
    return <Notice>{translate("noOrg")}</Notice>;
  }
  if (config.isPending) {
    return <Notice>{translate("loading")}</Notice>;
  }

  const canManage = current.role === "owner" || current.role === "admin";
  // A 404 means no config yet (or the feature is off); either way there is nothing to show, so fall
  // through to the form and let a save clarify with the specific message.
  const existing = config.data?.status === 200 ? config.data.data : null;
  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: getGetLlmConfigQueryKey(orgId) });

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (apiKey.trim() === "" || (needsBaseUrl && baseUrl.trim() === "")) {
      return;
    }
    save.mutate(
      {
        orgId,
        data: {
          provider,
          model: model.trim() || DEFAULT_MODEL,
          baseUrl: baseUrl.trim() || null,
          apiKey: apiKey.trim(),
        },
      },
      {
        onSuccess: () => {
          setApiKey("");
          invalidate();
        },
      },
    );
  };

  const providerOptions = PROVIDERS.map((option) => ({
    value: option,
    label: translate(`providers.${option}`),
  }));
  const modelOptions = models.map((option) => ({ value: option.id, label: option.displayName }));

  return (
    <section className="flex flex-col gap-4">
      <div>
        <h2 className="font-heading text-lg font-semibold text-foreground">{translate("title")}</h2>
        <p className="mt-1 text-sm text-muted-foreground">{translate("description")}</p>
      </div>

      {existing !== null ? (
        <dl className="grid grid-cols-[auto_1fr] gap-x-6 gap-y-1.5 text-sm">
          <dt className="text-muted-foreground">{translate("provider")}</dt>
          <dd className="text-foreground">{existing.provider}</dd>
          <dt className="text-muted-foreground">{translate("model")}</dt>
          <dd className="text-foreground">{existing.model}</dd>
          <dt className="text-muted-foreground">{translate("updated")}</dt>
          <dd className="text-foreground">
            {format.dateTime(new Date(existing.updatedAt), {
              month: "short",
              day: "numeric",
              year: "numeric",
            })}
          </dd>
        </dl>
      ) : null}

      {!canManage ? (
        <p className="text-sm text-muted-foreground">{translate("adminOnly")}</p>
      ) : (
        <form onSubmit={submit} className="flex flex-col gap-3">
          <Field id="provider-provider" label={translate("providerLabel")}>
            <Combobox
              id="provider-provider"
              value={provider}
              onValueChange={(value) => {
                setProvider(value);
                setModels([]); // the model list is provider-specific; reload after switching
              }}
              options={providerOptions}
              className="w-full"
              searchPlaceholder={tCommon("comboboxSearch")}
              emptyText={tCommon("comboboxEmpty")}
            />
          </Field>
          {needsBaseUrl ? (
            <Field id="provider-base-url" label={translate("baseUrlLabel")}>
              <input
                id="provider-base-url"
                value={baseUrl}
                onChange={(event) => setBaseUrl(event.target.value)}
                placeholder="https://api.openai.com/v1"
                className={FIELD_CLASS}
              />
            </Field>
          ) : null}
          <Field id="provider-model" label={translate("modelLabel")}>
            {models.length > 0 ? (
              <Combobox
                id="provider-model"
                value={model}
                onValueChange={setModel}
                options={modelOptions}
                className="w-full"
                searchPlaceholder={tCommon("comboboxSearch")}
                emptyText={tCommon("comboboxEmpty")}
              />
            ) : (
              <input
                id="provider-model"
                value={model}
                onChange={(event) => setModel(event.target.value)}
                className={FIELD_CLASS}
              />
            )}
          </Field>
          <Field
            id="provider-key"
            label={existing !== null ? translate("keyReplaceLabel") : translate("keyLabel")}
          >
            <input
              id="provider-key"
              type="password"
              value={apiKey}
              onChange={(event) => setApiKey(event.target.value)}
              placeholder="sk-..."
              autoComplete="off"
              className={FIELD_CLASS}
            />
          </Field>

          {/* Load the models the key can use into the picker above. Uses the entered key, or the stored
              one when none is typed (so an existing config can be re-modeled). */}
          <div>
            <button
              type="button"
              onClick={loadModels}
              disabled={listModels.isPending}
              className={SECONDARY_BUTTON_CLASS}
            >
              {listModels.isPending ? translate("loadingModels") : translate("loadModels")}
            </button>
            {listModels.isError ? (
              <p role="alert" className="mt-2 text-sm text-error">
                {translate("modelsError")}
              </p>
            ) : null}
          </div>

          {save.isError ? (
            <p role="alert" className="text-sm text-error">
              {translate(saveErrorKey(save.error))}
            </p>
          ) : null}

          <div className="flex items-center gap-2">
            <button type="submit" disabled={save.isPending} className={PRIMARY_BUTTON_CLASS}>
              {save.isPending ? translate("saving") : translate("save")}
            </button>
            {existing !== null ? (
              <button
                type="button"
                disabled={remove.isPending}
                onClick={() => remove.mutate({ orgId }, { onSuccess: invalidate })}
                className={SECONDARY_BUTTON_CLASS}
              >
                {translate("remove")}
              </button>
            ) : null}
          </div>
        </form>
      )}
    </section>
  );
}
