import { AuthForm } from "@/src/auth/auth-form";
import { AuthMode } from "@/src/auth/auth-mode";

export default function LoginPage() {
  return <AuthForm mode={AuthMode.Login} />;
}
