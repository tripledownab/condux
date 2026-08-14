"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useFormatter, useTranslations } from "next-intl";
import { type FormEvent, useEffect, useState } from "react";
import type { ConduxApiError } from "@/src/api/fetcher";
import {
  getGetSsoConfigQueryKey,
  useDeleteSsoConfig,
  useGetSsoConfig,
  useSetSsoConfig,
} from "@/src/api/generated/condux";
import {
  FIELD_CLASS,
  Field,
  PRIMARY_BUTTON_CLASS,
  SECONDARY_BUTTON_CLASS,
} from "@/src/components/form";
import { Notice } from "@/src/components/notice";
import { Combobox } from "@/src/components/ui/combobox";
import { OrgStatus, useCurrentOrg } from "@/src/orgs/current-org";

// Mirrors the backend SsoProtocol enum (sso_configs.protocol).
enum SsoProtocol {
  Oidc = 0,
  Saml = 1,
}

// Map a save failure to a message key. email_domain_taken and sso_requires_upgrade are both 409, so key on
// the error code first; then the feature-off (404), a rejected field (400) and the role gap (403).
function saveErrorKey(error: unknown): string {
  const e = error as ConduxApiError | null;
  if (e?.code === "email_domain_taken") {
    return "domainTaken";
  }
  if (e?.code === "sso_requires_upgrade" || e?.status === 409) {
    return "requiresUpgrade";
  }
  if (e?.status === 404) {
    return "notConfigured";
  }
  if (e?.status === 400) {
    return "invalidRequest";
  }
  if (e?.status === 403) {
    return "adminOnly";
  }
  return "failed";
}

// The enterprise-SSO settings tab (#72 + ADR-0032 slice 2): an org registers its IdP so its members sign
// in through it, routed by email domain. The IdP speaks OIDC or SAML; each protocol has its own fields and
// its own values to register on the IdP side (the OIDC redirect URI, or the SAML ACS URL + SP entity ID).
// The OIDC client secret is encrypted server-side and never read back, so saving re-sends it; the SAML
// certificate is the IdP's public signing certificate and echoes back. Managing is admin+; the tab is
// gated on the plan's Sso feature (a save on a non-SSO tier returns the upgrade message).
export function SsoSettings() {
  const translate = useTranslations("settings.sso");
  const tCommon = useTranslations("common");
  const format = useFormatter();
  const queryClient = useQueryClient();
  const current = useCurrentOrg();
  const orgReady = current.status === OrgStatus.Ready;
  const orgId = orgReady ? current.org.id : 0;

  const config = useGetSsoConfig(orgId, { query: { enabled: orgReady } });
  const save = useSetSsoConfig();
  const remove = useDeleteSsoConfig();

  const [protocol, setProtocol] = useState(SsoProtocol.Oidc);
  const [emailDomain, setEmailDomain] = useState("");
  const [issuer, setIssuer] = useState("");
  const [authorizationEndpoint, setAuthorizationEndpoint] = useState("");
  const [tokenEndpoint, setTokenEndpoint] = useState("");
  const [clientId, setClientId] = useState("");
  const [clientSecret, setClientSecret] = useState("");
  const [samlSsoUrl, setSamlSsoUrl] = useState("");
  const [samlCertificate, setSamlCertificate] = useState("");
  // What the org's admin registers in the IdP — this app's own endpoints (resolved on the client).
  const [origin, setOrigin] = useState("");
  useEffect(() => {
    setOrigin(window.location.origin);
  }, []);

  // A 404 means no config yet (or the feature is off); either way there is nothing to show, so fall
  // through to the form and let a save clarify with the specific message.
  const existing = config.data?.status === 200 ? config.data.data : null;
  const existingProtocol = existing?.protocol;
  useEffect(() => {
    if (existingProtocol !== undefined) {
      setProtocol(existingProtocol);
    }
  }, [existingProtocol]);

  if (current.status === OrgStatus.Loading || config.isPending) {
    return <Notice>{translate("loading")}</Notice>;
  }
  if (current.status === OrgStatus.Error) {
    return <Notice>{translate("error")}</Notice>;
  }
  if (current.status === OrgStatus.NoOrg) {
    return <Notice>{translate("noOrg")}</Notice>;
  }

  const canManage = current.role === "owner" || current.role === "admin";
  const saml = protocol === SsoProtocol.Saml;
  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: getGetSsoConfigQueryKey(orgId) });

  const protocolOptions = [
    { value: String(SsoProtocol.Oidc), label: translate("protocolOidc") },
    { value: String(SsoProtocol.Saml), label: translate("protocolSaml") },
  ];

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    save.mutate(
      {
        orgId,
        data: {
          emailDomain: emailDomain.trim(),
          issuer: issuer.trim(),
          protocol,
          authorizationEndpoint: saml ? null : authorizationEndpoint.trim(),
          tokenEndpoint: saml ? null : tokenEndpoint.trim(),
          clientId: saml ? null : clientId.trim(),
          clientSecret: saml ? null : clientSecret,
          samlSsoUrl: saml ? samlSsoUrl.trim() : null,
          samlCertificate: saml ? samlCertificate.trim() : null,
        },
      },
      {
        onSuccess: () => {
          setClientSecret("");
          invalidate();
        },
      },
    );
  };

  return (
    <section className="flex flex-col gap-4">
      <div>
        <h2 className="font-heading text-lg font-semibold text-foreground">{translate("title")}</h2>
        <p className="mt-1 text-sm text-muted-foreground">{translate("description")}</p>
      </div>

      <dl className="grid grid-cols-[auto_1fr] gap-x-6 gap-y-1.5 text-sm">
        {saml ? (
          <>
            <dt className="text-muted-foreground">{translate("acsUrlLabel")}</dt>
            <dd className="break-all font-mono text-foreground">{`${origin}/api/auth/sso/saml/acs`}</dd>
            <dt className="text-muted-foreground">{translate("spEntityIdLabel")}</dt>
            <dd className="break-all font-mono text-foreground">{`${origin}/api/auth/sso/saml`}</dd>
          </>
        ) : (
          <>
            <dt className="text-muted-foreground">{translate("callbackLabel")}</dt>
            <dd className="break-all font-mono text-foreground">{`${origin}/api/auth/sso/callback`}</dd>
          </>
        )}
        {existing !== null ? (
          <>
            <dt className="text-muted-foreground">{translate("protocolLabel")}</dt>
            <dd className="text-foreground">
              {existing.protocol === SsoProtocol.Saml
                ? translate("protocolSaml")
                : translate("protocolOidc")}
            </dd>
            <dt className="text-muted-foreground">{translate("emailDomainLabel")}</dt>
            <dd className="text-foreground">{existing.emailDomain}</dd>
            <dt className="text-muted-foreground">
              {existing.protocol === SsoProtocol.Saml
                ? translate("samlIssuerLabel")
                : translate("issuerLabel")}
            </dt>
            <dd className="break-all text-foreground">{existing.issuer}</dd>
            <dt className="text-muted-foreground">{translate("updated")}</dt>
            <dd className="text-foreground">
              {format.dateTime(new Date(existing.updatedAt), {
                month: "short",
                day: "numeric",
                year: "numeric",
              })}
            </dd>
          </>
        ) : null}
      </dl>

      {!canManage ? (
        <p className="text-sm text-muted-foreground">{translate("adminOnly")}</p>
      ) : (
        <form onSubmit={submit} className="flex flex-col gap-3">
          <Field id="sso-protocol" label={translate("protocolLabel")}>
            <Combobox
              value={String(protocol)}
              onValueChange={(value) => setProtocol(Number(value))}
              options={protocolOptions}
              aria-label={translate("protocolLabel")}
              searchPlaceholder={tCommon("comboboxSearch")}
              emptyText={tCommon("comboboxEmpty")}
            />
          </Field>
          <Field id="sso-domain" label={translate("emailDomainLabel")}>
            <input
              id="sso-domain"
              value={emailDomain}
              onChange={(event) => setEmailDomain(event.target.value)}
              placeholder="acme.com"
              className={FIELD_CLASS}
            />
          </Field>
          <Field
            id="sso-issuer"
            label={saml ? translate("samlIssuerLabel") : translate("issuerLabel")}
          >
            <input
              id="sso-issuer"
              value={issuer}
              onChange={(event) => setIssuer(event.target.value)}
              placeholder="https://idp.example"
              className={FIELD_CLASS}
            />
          </Field>
          {saml ? (
            <>
              <Field id="sso-saml-url" label={translate("samlSsoUrlLabel")}>
                <input
                  id="sso-saml-url"
                  value={samlSsoUrl}
                  onChange={(event) => setSamlSsoUrl(event.target.value)}
                  placeholder="https://idp.example/sso/saml"
                  className={FIELD_CLASS}
                />
              </Field>
              <Field id="sso-saml-cert" label={translate("samlCertificateLabel")}>
                <textarea
                  id="sso-saml-cert"
                  value={samlCertificate}
                  onChange={(event) => setSamlCertificate(event.target.value)}
                  placeholder="-----BEGIN CERTIFICATE-----"
                  rows={6}
                  className={`${FIELD_CLASS} font-mono`}
                />
              </Field>
            </>
          ) : (
            <>
              <Field id="sso-authz" label={translate("authorizationEndpointLabel")}>
                <input
                  id="sso-authz"
                  value={authorizationEndpoint}
                  onChange={(event) => setAuthorizationEndpoint(event.target.value)}
                  placeholder="https://idp.example/authorize"
                  className={FIELD_CLASS}
                />
              </Field>
              <Field id="sso-token" label={translate("tokenEndpointLabel")}>
                <input
                  id="sso-token"
                  value={tokenEndpoint}
                  onChange={(event) => setTokenEndpoint(event.target.value)}
                  placeholder="https://idp.example/token"
                  className={FIELD_CLASS}
                />
              </Field>
              <Field id="sso-client-id" label={translate("clientIdLabel")}>
                <input
                  id="sso-client-id"
                  value={clientId}
                  onChange={(event) => setClientId(event.target.value)}
                  className={FIELD_CLASS}
                />
              </Field>
              <Field
                id="sso-client-secret"
                label={
                  existing !== null
                    ? translate("clientSecretReplaceLabel")
                    : translate("clientSecretLabel")
                }
              >
                <input
                  id="sso-client-secret"
                  type="password"
                  value={clientSecret}
                  onChange={(event) => setClientSecret(event.target.value)}
                  autoComplete="off"
                  className={FIELD_CLASS}
                />
              </Field>
            </>
          )}

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
