namespace Condux.Core.Auth;

/// <summary>The protocol an org's enterprise-SSO IdP speaks (sso_configs.protocol). OIDC stores the
/// authorization/token endpoints + client credentials; SAML stores the Single Sign-On URL + the IdP's
/// public signing certificate, with the issuer column holding the IdP entity ID.</summary>
public enum SsoProtocol
{
    Oidc = 0,
    Saml = 1,
}
