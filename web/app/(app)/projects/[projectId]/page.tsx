import { PageContainer } from "@/src/components/page-container";
import { ProjectDetail } from "@/src/projects/project-detail";

export default async function ProjectDetailPage({
  params,
}: {
  params: Promise<{ projectId: string }>;
}) {
  // projectId is the project's public UUID (#125); the component resolves it via getProject.
  const { projectId } = await params;
  return (
    <PageContainer>
      <ProjectDetail publicId={projectId} />
    </PageContainer>
  );
}
