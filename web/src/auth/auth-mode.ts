// Which auth flow the shared form renders. Lives in its own module (not the "use client" auth-form)
// so both the server-component pages and the client form import the real enum value, not a client
// reference across the boundary.
export enum AuthMode {
  Login = "login",
  Signup = "signup",
}
