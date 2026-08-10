import { IssueDetail } from "@/src/issues/issue-detail";

export default async function IssueDetailPage({
  params,
}: {
  params: Promise<{ issueId: string }>;
}) {
  const { issueId } = await params;
  return <IssueDetail issueId={issueId} />;
}
