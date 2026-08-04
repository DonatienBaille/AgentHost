{{- define "agenthost.fullname" -}}
{{ .Release.Name }}-agenthost
{{- end -}}

{{- define "agenthost.labels" -}}
app.kubernetes.io/name: agenthost
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}
