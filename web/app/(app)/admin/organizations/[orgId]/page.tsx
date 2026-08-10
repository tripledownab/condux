import { AdminOrgDetail } from "@/src/admin/admin-org-detail";

export default async function AdminOrgDetailPage({
  params,
}: {
  params: Promise<{ orgId: string }>;
}) {
  const { orgId } = await params;
  return <AdminOrgDetail orgId={Number(orgId)} />;
}
