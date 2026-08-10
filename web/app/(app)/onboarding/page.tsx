import { PageContainer } from "@/src/components/page-container";
import { Onboarding } from "@/src/onboarding/onboarding";
import { ReplayBanner } from "@/src/onboarding/replay-banner";

export default function OnboardingPage() {
  return (
    <PageContainer>
      <ReplayBanner />
      <Onboarding />
    </PageContainer>
  );
}
