export interface Artifact {
  id: string;
  runId: string;
  name: string;
  artifactType: string | null;
  sizeBytes: number | null;
  createdAt: string;
}
