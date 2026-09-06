{{/* Common labels applied to every object. */}}
{{- define "condux.labels" -}}
app.kubernetes.io/name: condux
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version }}
{{- end -}}

{{/* Selector labels for a component (stable across upgrades — never add version here). */}}
{{- define "condux.selectorLabels" -}}
app.kubernetes.io/name: condux
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/component: {{ .component }}
{{- end -}}

{{/* The Secret holding credentials: an operator-provided existing one, or the chart-created default. */}}
{{- define "condux.secretName" -}}
{{- if .Values.secret.existingSecret -}}{{ .Values.secret.existingSecret }}{{- else -}}{{ .Release.Name }}-condux{{- end -}}
{{- end -}}

{{/* Image reference for a service, e.g. ghcr.io/condux/relay:latest. Pass the image name as .name. */}}
{{- define "condux.image" -}}
{{ .root.Values.image.registry }}/{{ .name }}:{{ .root.Values.image.tag }}
{{- end -}}

{{/* CONDUX_POSTGRES from the Secret — every .NET service needs it. Call with the root context. */}}
{{- define "condux.postgresEnv" -}}
- name: CONDUX_POSTGRES
  valueFrom:
    secretKeyRef:
      name: {{ include "condux.secretName" . }}
      key: CONDUX_POSTGRES
{{- end -}}

{{/* CONDUX_KAFKA_BOOTSTRAP from external-store config. */}}
{{- define "condux.kafkaEnv" -}}
- name: CONDUX_KAFKA_BOOTSTRAP
  value: {{ .Values.externalStores.kafka.bootstrap | quote }}
{{- end -}}

{{/* ClickHouse URL (config) + user/password (Secret). Used by control-plane, consumer, conductor. */}}
{{- define "condux.clickhouseEnv" -}}
- name: CONDUX_CLICKHOUSE_URL
  value: {{ .Values.externalStores.clickhouse.url | quote }}
- name: CONDUX_CLICKHOUSE_USER
  valueFrom:
    secretKeyRef:
      name: {{ include "condux.secretName" . }}
      key: CONDUX_CLICKHOUSE_USER
- name: CONDUX_CLICKHOUSE_PASSWORD
  valueFrom:
    secretKeyRef:
      name: {{ include "condux.secretName" . }}
      key: CONDUX_CLICKHOUSE_PASSWORD
{{- end -}}

{{/* The public host of the relay, stated once. The minted DSNs, the ingress rule and NOTES all read it
here, because an SDK reaches the relay by the host in its DSN, so an install that routes one host while
minting DSNs for another routes nothing. Required on purpose: the code's own default is the dev value
localhost:9010, so an unset host mints DSNs that reach no relay while every pod reports healthy. */}}
{{- define "condux.ingestHost" -}}
{{- required "config.ingestHost is required: the public host of your relay, e.g. ingest.example.com. Minted DSNs and the ingress rule both point at it." .Values.config.ingestHost -}}
{{- end -}}

{{/* CONDUX_INGEST_SCHEME/HOST: the address the control-plane writes into every DSN it mints. */}}
{{- define "condux.ingestEnv" -}}
- name: CONDUX_INGEST_SCHEME
  value: {{ .Values.config.ingestScheme | quote }}
- name: CONDUX_INGEST_HOST
  value: {{ include "condux.ingestHost" . | quote }}
{{- end -}}

{{/* CONDUX_APP_BASE_URL: the absolute dashboard URL that links in email point at. Both mailers need it,
the control-plane for the invite accept link and the consumer for the weekly summary CTA, and they
degrade differently without it (values.yaml says how). Emit only inside `{{- if .Values.config.appBaseUrl }}`. */}}
{{- define "condux.appBaseUrlEnv" -}}
- name: CONDUX_APP_BASE_URL
  value: {{ .Values.config.appBaseUrl | quote }}
{{- end -}}

{{/* SMTP config (host/port/ssl/from/user) + password (Secret). Used by control-plane + consumer for
email delivery. Emit only inside `{{- if .Values.email.enabled }}`. */}}
{{- define "condux.smtpEnv" -}}
- name: CONDUX_SMTP_HOST
  value: {{ .Values.email.host | quote }}
- name: CONDUX_SMTP_PORT
  value: {{ .Values.email.port | quote }}
- name: CONDUX_SMTP_SSL
  value: {{ .Values.email.ssl | quote }}
- name: CONDUX_SMTP_FROM
  value: {{ .Values.email.from | quote }}
- name: CONDUX_SMTP_USER
  value: {{ .Values.email.user | quote }}
{{- if .Values.email.password }}
- name: CONDUX_SMTP_PASSWORD
  valueFrom:
    secretKeyRef:
      name: {{ include "condux.secretName" . }}
      key: CONDUX_SMTP_PASSWORD
{{- end }}
{{- end -}}
