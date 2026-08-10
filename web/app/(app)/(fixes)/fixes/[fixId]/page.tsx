import { FixDetail } from "@/src/fixes/fix-detail";

export default async function FixDetailPage({ params }: { params: Promise<{ fixId: string }> }) {
  const { fixId } = await params;
  return <FixDetail fixId={fixId} />;
}
