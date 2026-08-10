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
