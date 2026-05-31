# Authentication & Authorization in Microservices — Complete Guide

## The System We're Working With

```
┌─────────────────────────────────────────────────────────────────────────┐
│                          EXAMPLE: ShopEasy                             │
│                     (E-Commerce Microservice System)                   │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                        │
│  Clients:                                                              │
│  ├── React SPA (Web App)                                               │
│  └── Mobile App (iOS / Android)                                        │
│                                                                        │
│  Identity Provider (IdP / Authorization Server):                       │
│  └── Duende IdentityServer (alternatives: Azure AD, Auth0, Keycloak)   │
│                                                                        │
│  API Gateway:                                                          │
│  └── YARP or Ocelot (also acts as BFF for the React SPA)               │
│                                                                        │
│  Microservices:                                                        │
│  ├── Product Service     — product catalog, CRUD                       │
│  ├── Order Service       — placing and tracking orders                 │
│  ├── Inventory Service   — stock management                            │
│  ├── Payment Service     — charging customers                          │
│  └── Notification Service — emails, SMS, push notifications            │
│                                                                        │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## Part 1: The Players and Their Roles

### Identity Provider (IdP / Authorization Server)

This is a **standalone, dedicated service** whose only job is managing identity and issuing tokens.
In our example: **Duende IdentityServer** (an open-source .NET library you host yourself).
Other popular options: **Azure AD / Entra ID** (Microsoft's cloud IdP), **Auth0** (SaaS IdP), **Keycloak** (open-source, Java-based).

What it stores:
- User accounts (usernames, hashed passwords, profile info)
- Client registrations (which apps are allowed to authenticate)
- Scopes and API resources (what permissions exist in the system)
- Its own **private key** (used to sign tokens)

What it exposes publicly:
- A **login page** (where users type credentials)
- A **token endpoint** (`/connect/token` — where authorization codes are exchanged for tokens)
- A **discovery endpoint** (`/.well-known/openid-configuration` — metadata about the IdP)
- A **JWKS endpoint** (`/.well-known/jwks` — the public keys anyone can use to validate tokens)

### API Gateway (also acting as BFF for the web app)

This is the **single entry point** for all client applications.
In our example: **YARP** (Yet Another Reverse Proxy — Microsoft's modern .NET proxy).

For the React SPA, the Gateway plays a dual role:
- **BFF (Backend-for-Frontend):** Handles the OAuth flow on behalf of the browser, stores tokens, manages cookies
- **Reverse Proxy:** Routes requests to the correct microservice

For the Mobile App, the Gateway is simpler:
- **Reverse Proxy only:** The mobile app manages its own tokens; the Gateway just validates and routes

### Microservices (Product, Order, Inventory, Payment, Notification)

These services **never handle the OAuth login flow**. They are purely consumers of tokens.
Their auth-related responsibilities are:
- Validate incoming Access Tokens (using the IdP's public key)
- Read claims from the token (user ID, roles, scopes) to make authorization decisions
- Optionally: obtain their own tokens via Client Credentials Grant for service-to-service calls

---

## Part 2: One-Time Setup (Before Any Request Happens)

Before the system can handle any authentication, the following registration and configuration must happen:

### Step A — Register Clients in the IdP

Every application that needs to **initiate** an OAuth flow must be registered in the IdP.

```
┌─────────────────────────────────────────────────────────────────┐
│              CLIENT REGISTRATIONS IN THE IdP                    │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Client 1: "shopeasy-web"                                       │
│  ├── Type: Confidential (has a backend that can keep a secret)  │
│  ├── Client ID: shopeasy-web                                    │
│  ├── Client Secret: K8s2mP9xQ4wR7vL... (stored in Gateway)     │
│  ├── Allowed Grant Type: Authorization Code                     │
│  ├── Redirect URI: https://gateway.shopeasy.com/callback        │
│  ├── Allowed Scopes: openid, profile, email,                    │
│  │                   products.read, orders.fullaccess            │
│  └── Token Lifetime: Access=1hr, Refresh=30days                 │
│                                                                 │
│  Client 2: "shopeasy-mobile"                                    │
│  ├── Type: Public (cannot keep a secret — code can be reversed) │
│  ├── Client ID: shopeasy-mobile                                 │
│  ├── Client Secret: NONE (uses PKCE instead)                    │
│  ├── Allowed Grant Type: Authorization Code + PKCE              │
│  ├── Redirect URI: shopeasy://callback                          │
│  ├── Allowed Scopes: openid, profile, email,                    │
│  │                   products.read, orders.fullaccess            │
│  └── Token Lifetime: Access=30min, Refresh=90days               │
│                                                                 │
│  Client 3: "order-service"                                      │
│  ├── Type: Confidential (server-side service)                   │
│  ├── Client ID: order-service                                   │
│  ├── Client Secret: Tm4pQx8rW2kJ... (stored in Order Service)  │
│  ├── Allowed Grant Type: Client Credentials                     │
│  ├── Redirect URI: NONE (no browser involved)                   │
│  ├── Allowed Scopes: inventory.read, payment.charge             │
│  └── Token Lifetime: Access=15min                               │
│                                                                 │
│  (Similar entries for any service that calls other services)     │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

Notice:
- The **React SPA** is NOT registered directly — the **Gateway** is registered on its behalf (because the Gateway handles the OAuth flow for the SPA)
- The **Mobile App** IS registered directly (because it handles its own OAuth flow)
- **Order Service** is registered because it needs to call other services as itself (Client Credentials)
- **Product Service** is NOT registered (it only receives and validates tokens, never requests them)

### Step B — Configure Each Microservice to Trust the IdP

Every microservice adds middleware that says "I trust tokens from this IdP":

```csharp
// In each microservice's Program.cs (Product, Order, Inventory, etc.)
builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer("Bearer", options =>
    {
        // Point to the IdP — the middleware will automatically
        // fetch the public keys from /.well-known/openid-configuration
        options.Authority = "https://idp.shopeasy.com";

        // This service only accepts tokens intended for it
        options.Audience = "shopeasy-api";
    });
```

No client ID, no client secret, no registration needed. The service just needs to know the IdP's address. The middleware fetches the public keys automatically and caches them.

---

## Part 3: Flow A — User Logs In via React SPA (Web App)

```
┌────────┐      ┌──────────┐      ┌────────────┐      ┌────────────┐
│ Browser │      │  Gateway  │     │     IdP      │     │  Services  │
│ (React) │      │(BFF+Proxy)│     │(IdentitySvr)│     │            │
└───┬─────┘      └─────┬─────┘     └──────┬──────┘     └─────┬──────┘
    │                   │                   │                  │
    │ 1. Click "Login"  │                   │                  │
    ├──────────────────>│                   │                  │
    │                   │                   │                  │
    │ 2. Gateway builds │the authorization  │                  │
    │    URL and sends  │a redirect (302)   │                  │
    │<──────────────────┤                   │                  │
    │                   │                   │                  │
    │ 3. Browser follows redirect to IdP login page            │
    ├───────────────────────────────────────>│                  │
    │                   │                   │                  │
    │   ┌───────────────────────────────────┐│                  │
    │   │ User sees IdP login page.         ││                  │
    │   │ Types email + password.           ││                  │
    │   │ Clicks "Sign In".                 ││                  │
    │   │                                   ││                  │
    │   │ The React app NEVER sees the      ││                  │
    │   │ credentials. The browser is on    ││                  │
    │   │ the IdP's domain, not yours.      ││                  │
    │   └───────────────────────────────────┘│                  │
    │                   │                   │                  │
    │ 4. IdP validates credentials, creates Authorization Code │
    │    IdP redirects browser back to Gateway's callback URL  │
    │    https://gateway.shopeasy.com/callback?code=abc123     │
    │<──────────────────────────────────────┤                  │
    │                   │                   │                  │
    │ 5. Browser hits Gateway's /callback endpoint             │
    ├──────────────────>│                   │                  │
    │                   │                   │                  │
    │                   │ 6. Gateway makes  │                  │
    │                   │ server-to-server  │                  │
    │                   │ POST to IdP:      │                  │
    │                   │                   │                  │
    │                   │ POST /connect/token                  │
    │                   │ grant_type=authorization_code         │
    │                   │ code=abc123                           │
    │                   │ client_id=shopeasy-web                │
    │                   │ client_secret=K8s2mP9x...            │
    │                   ├──────────────────>│                  │
    │                   │                   │                  │
    │                   │ 7. IdP validates: │                  │
    │                   │  - Is this code valid?                │
    │                   │  - Was it issued for this client?     │
    │                   │  - Has it been used before?           │
    │                   │  - Does redirect_uri match?           │
    │                   │                   │                  │
    │                   │ 8. IdP responds   │                  │
    │                   │ with tokens:      │                  │
    │                   │  - Access Token   │                  │
    │                   │  - ID Token       │                  │
    │                   │  - Refresh Token  │                  │
    │                   │<──────────────────┤                  │
    │                   │                   │                  │
    │ 9. Gateway stores tokens              │                  │
    │    (encrypted inside an HTTP-only     │                  │
    │    cookie, or in server-side session  │                  │
    │    store like Redis with a session ID │                  │
    │    in the cookie)                     │                  │
    │                   │                   │                  │
    │ 10. Gateway sets the cookie on the    │                  │
    │     browser and redirects to the app  │                  │
    │<──────────────────┤                   │                  │
    │                   │                   │                  │
    │   ┌──────────────────────────────────┐│                  │
    │   │ LOGIN IS COMPLETE.               ││                  │
    │   │                                  ││                  │
    │   │ The browser now has an encrypted ││                  │
    │   │ HTTP-only cookie. React has ZERO ││                  │
    │   │ awareness of tokens. No token in ││                  │
    │   │ localStorage. No interceptor.    ││                  │
    │   └──────────────────────────────────┘│                  │
    │                   │                   │                  │
    │ === SUBSEQUENT REQUESTS (e.g., "GET /api/orders") ====== │
    │                   │                   │                  │
    │ 11. React calls:  │                   │                  │
    │     fetch('/api/orders')              │                  │
    │     (no Authorization header — just   │                  │
    │      a normal request; the browser    │                  │
    │      automatically attaches the       │                  │
    │      cookie)                          │                  │
    ├──────────────────>│                   │                  │
    │                   │                   │                  │
    │                   │ 12. Gateway reads │                  │
    │                   │ cookie, extracts  │                  │
    │                   │ Access Token,     │                  │
    │                   │ attaches it as:   │                  │
    │                   │ Authorization:    │                  │
    │                   │ Bearer eyJhb...   │                  │
    │                   ├──────────────────────────────────────>│
    │                   │                   │                  │
    │                   │                   │     13. Order    │
    │                   │                   │     Service      │
    │                   │                   │     validates    │
    │                   │                   │     the token    │
    │                   │                   │     (using IdP's │
    │                   │                   │     public key)  │
    │                   │                   │     and reads    │
    │                   │                   │     claims:      │
    │                   │                   │     sub=user-42  │
    │                   │                   │     role=customer│
    │                   │                   │                  │
    │                   │     14. Response: │  order data      │
    │                   │<─────────────────────────────────────┤
    │  15. JSON response│                   │                  │
    │<──────────────────┤                   │                  │
    │                   │                   │                  │
```

### Token Refresh (happens transparently):

```
    │                   │                   │                  │
    │ 16. React calls   │                   │                  │
    │     fetch('/api/orders')              │                  │
    ├──────────────────>│                   │                  │
    │                   │                   │                  │
    │                   │ 17. Gateway reads │                  │
    │                   │ cookie, sees the  │                  │
    │                   │ Access Token has  │                  │
    │                   │ expired.          │                  │
    │                   │                   │                  │
    │                   │ 18. Gateway uses  │                  │
    │                   │ the Refresh Token │                  │
    │                   │ to get a new      │                  │
    │                   │ Access Token:     │                  │
    │                   │                   │                  │
    │                   │ POST /connect/token                  │
    │                   │ grant_type=refresh_token              │
    │                   │ refresh_token=def456...               │
    │                   │ client_id=shopeasy-web                │
    │                   │ client_secret=K8s2mP9x...            │
    │                   ├──────────────────>│                  │
    │                   │                   │                  │
    │                   │ 19. IdP returns   │                  │
    │                   │ new Access Token  │                  │
    │                   │ (and optionally   │                  │
    │                   │ rotates the       │                  │
    │                   │ Refresh Token)    │                  │
    │                   │<──────────────────┤                  │
    │                   │                   │                  │
    │                   │ 20. Gateway       │                  │
    │                   │ updates the cookie│                  │
    │                   │ with new tokens,  │                  │
    │                   │ proceeds with the │                  │
    │                   │ original request  │                  │
    │                   │ to Order Service  │                  │
    │                   ├──────────────────────────────────────>│
    │                   │                   │                  │
    │   ┌──────────────────────────────────┐│                  │
    │   │ The user never saw any of this.  ││                  │
    │   │ No re-login. No interruption.    ││                  │
    │   │ The Gateway handled everything.  ││                  │
    │   └──────────────────────────────────┘│                  │
    │                   │                   │                  │
```

---

## Part 4: Flow B — User Logs In via Mobile App

```
┌────────────┐       ┌────────────┐       ┌──────────┐       ┌──────────┐
│ Mobile App │       │     IdP     │       │  Gateway  │       │ Services │
│ (iOS/And)  │       │(IdentitySvr)│      │  (Proxy   │       │          │
│            │       │             │       │   only)   │       │          │
└─────┬──────┘       └──────┬──────┘      └─────┬─────┘       └────┬─────┘
      │                      │                   │                  │
      │ 1. User taps "Login" │                   │                  │
      │                      │                   │                  │
      │ 2. App generates PKCE pair:              │                  │
      │    code_verifier = random string         │                  │
      │    code_challenge = SHA256(code_verifier) │                  │
      │                      │                   │                  │
      │ 3. App opens system  │                   │                  │
      │    browser to IdP:   │                   │                  │
      │    /authorize?        │                   │                  │
      │    client_id=shopeasy-mobile              │                  │
      │    &code_challenge=Xy9...                 │                  │
      │    &code_challenge_method=S256            │                  │
      │    &redirect_uri=shopeasy://callback      │                  │
      ├─────────────────────>│                   │                  │
      │                      │                   │                  │
      │ 4. User enters       │                   │                  │
      │    credentials on    │                   │                  │
      │    IdP login page    │                   │                  │
      │                      │                   │                  │
      │ 5. IdP redirects to: │                   │                  │
      │    shopeasy://callback?code=xyz789        │                  │
      │    (mobile OS catches│this custom URL     │                  │
      │    scheme and hands  │the code to the app)│                  │
      │<─────────────────────┤                   │                  │
      │                      │                   │                  │
      │ 6. App exchanges code│for tokens:        │                  │
      │    POST /connect/token                   │                  │
      │    grant_type=authorization_code          │                  │
      │    code=xyz789                            │                  │
      │    client_id=shopeasy-mobile              │                  │
      │    code_verifier=original_random_string   │                  │
      │    (NO client_secret — PKCE replaces it)  │                  │
      ├─────────────────────>│                   │                  │
      │                      │                   │                  │
      │ 7. IdP validates:    │                   │                  │
      │    SHA256(code_verifier) == code_challenge?                  │
      │    Yes → tokens issued                   │                  │
      │                      │                   │                  │
      │ 8. IdP responds with:│                   │                  │
      │    Access Token      │                   │                  │
      │    ID Token          │                   │                  │
      │    Refresh Token     │                   │                  │
      │<─────────────────────┤                   │                  │
      │                      │                   │                  │
      │ 9. App stores tokens │                   │                  │
      │    in secure storage │                   │                  │
      │    (iOS Keychain /   │                   │                  │
      │    Android Keystore) │                   │                  │
      │                      │                   │                  │
      │ === SUBSEQUENT REQUESTS ======================================
      │                      │                   │                  │
      │ 10. App calls Gateway│directly with      │                  │
      │     Authorization: Bearer eyJhb...       │                  │
      ├──────────────────────────────────────────>│                  │
      │                      │                   │                  │
      │                      │    11. Gateway     │                  │
      │                      │    validates token │                  │
      │                      │    (or just passes │                  │
      │                      │    it through) and │                  │
      │                      │    routes to the   │                  │
      │                      │    correct service │                  │
      │                      │                   ├─────────────────>│
      │                      │                   │                  │
      │                      │                   │  12. Service     │
      │                      │                   │  validates token │
      │                      │                   │  and responds    │
      │                      │                   │<─────────────────┤
      │                      │                   │                  │
      │ 13. Response         │                   │                  │
      │<─────────────────────────────────────────┤                  │
      │                      │                   │                  │
```

Key differences from web flow:
- **No cookie.** The mobile app stores and manages tokens directly.
- **No client secret.** PKCE replaces it (because mobile app code can be decompiled).
- **The app attaches the token itself** in the Authorization header (unlike the web flow where the Gateway does this).
- **Token refresh** is the app's responsibility — when it gets a 401, it uses the Refresh Token to get a new Access Token.

---

## Part 5: Flow C — Service-to-Service (Internal Communication)

### Scenario: Order Service needs to check stock in Inventory Service

```
┌──────────────┐       ┌────────────┐       ┌───────────────┐
│ Order Service │       │     IdP     │       │Inventory Svc  │
│              │       │(IdentitySvr)│       │               │
└──────┬───────┘       └──────┬──────┘       └───────┬───────┘
       │                      │                      │
       │  OPTION A: Token Forwarding (user context)  │
       │  ──────────────────────────────────────────  │
       │                      │                      │
       │  Order Service already has the user's       │
       │  Access Token (received from the Gateway    │
       │  in the Authorization header). It simply    │
       │  forwards it:                               │
       │                      │                      │
       │  GET /api/stock/product-123                 │
       │  Authorization: Bearer eyJhb... (user token)│
       ├─────────────────────────────────────────────>│
       │                      │                      │
       │  Inventory Service validates the same token │
       │  and knows "user-42 is asking about stock"  │
       │<─────────────────────────────────────────────┤
       │                      │                      │
       │                      │                      │
       │  OPTION B: Client Credentials (no user)     │
       │  ──────────────────────────────────────────  │
       │                      │                      │
       │  Used when there's no user context, e.g.,   │
       │  a background job syncing inventory nightly  │
       │                      │                      │
       │  1. Order Service authenticates ITSELF:     │
       │                      │                      │
       │  POST /connect/token │                      │
       │  grant_type=client_credentials              │
       │  client_id=order-service                    │
       │  client_secret=Tm4pQx8r...                  │
       │  scope=inventory.read                       │
       ├─────────────────────>│                      │
       │                      │                      │
       │  2. IdP returns an   │                      │
       │  Access Token for    │                      │
       │  "order-service"     │                      │
       │  (no user identity   │                      │
       │  in this token —     │                      │
       │  it represents the   │                      │
       │  service itself)     │                      │
       │<─────────────────────┤                      │
       │                      │                      │
       │  3. Order Service calls Inventory Service   │
       │  with its own token: │                      │
       │                      │                      │
       │  GET /api/stock/product-123                 │
       │  Authorization: Bearer eyJhb... (svc token) │
       ├─────────────────────────────────────────────>│
       │                      │                      │
       │  4. Inventory Service validates the token   │
       │  and sees: "this is order-service, it has   │
       │  the inventory.read scope → allowed"        │
       │<─────────────────────────────────────────────┤
       │                      │                      │
```

### Scenario: Async communication via Message Broker

```
┌──────────────┐    ┌──────────────┐    ┌───────────────┐   ┌────────────┐
│ Order Service │    │ Message Broker│    │Inventory Svc  │   │Payment Svc │
│              │    │  (RabbitMQ /  │    │               │   │            │
│              │    │  Azure SB)    │    │  (subscriber) │   │(subscriber)│
└──────┬───────┘    └──────┬───────┘    └───────┬───────┘   └─────┬──────┘
       │                   │                    │                  │
       │ 1. Publish event: │                    │                  │
       │ OrderConfirmed    │                    │                  │
       │ {                 │                    │                  │
       │   OrderId: 456,   │                    │                  │
       │   UserId: "u-42", │                    │                  │
       │   ProductId: 123, │                    │                  │
       │   Amount: $99.99  │                    │                  │
       │ }                 │                    │                  │
       ├──────────────────>│                    │                  │
       │                   │                    │                  │
       │  ┌────────────────────────────────────────────────────┐  │
       │  │ NO OAuth token in the message.                     │  │
       │  │ The broker itself is the security boundary.        │  │
       │  │ Only services with valid broker credentials        │  │
       │  │ (connection string / certificates) can connect.    │  │
       │  │ User identity is carried as DATA in the message    │  │
       │  │ payload (UserId field), not as a token.            │  │
       │  └────────────────────────────────────────────────────┘  │
       │                   │                    │                  │
       │                   │ 2. Broker delivers │                  │
       │                   │ to all subscribers │                  │
       │                   ├───────────────────>│                  │
       │                   ├──────────────────────────────────────>│
       │                   │                    │                  │
       │                   │   3. Inventory     │  3. Payment      │
       │                   │   reduces stock    │  charges user    │
       │                   │   for product-123  │  $99.99          │
       │                   │   by 1 unit        │                  │
       │                   │                    │                  │
```

---

## Part 6: What's Inside the Tokens

### Access Token (decoded JWT)

```json
{
  "header": {
    "alg": "RS256",          // Signed with RSA + SHA-256
    "typ": "JWT",
    "kid": "key-id-123"      // Which public key to use for validation
  },
  "payload": {
    "iss": "https://idp.shopeasy.com",         // Issuer: who created this token
    "sub": "user-42",                          // Subject: the user's unique ID
    "aud": "shopeasy-api",                     // Audience: which API this token is for
    "client_id": "shopeasy-web",               // Which client app requested this
    "scope": "openid profile orders.read",     // What this token allows
    "role": "customer",                        // Custom claim: user's role
    "exp": 1735693200,                         // Expires at (Unix timestamp)
    "iat": 1735689600,                         // Issued at
    "nbf": 1735689600                          // Not valid before
  },
  "signature": "Rb7mX2p9Qs..."   // Created with IdP's PRIVATE key
                                   // Validated with IdP's PUBLIC key
}
```

### ID Token (decoded JWT)

```json
{
  "payload": {
    "iss": "https://idp.shopeasy.com",
    "sub": "user-42",
    "aud": "shopeasy-web",            // Audience is the CLIENT APP (not the API)
    "name": "John Smith",             // User's display name
    "email": "john@example.com",      // User's email
    "email_verified": true,
    "picture": "https://...",         // Profile picture URL
    "at_hash": "HK6E_P6D...",        // Hash of the Access Token (for validation)
    "exp": 1735693200,
    "iat": 1735689600
  }
}
```

### Client Credentials Token (service-to-service, no user)

```json
{
  "payload": {
    "iss": "https://idp.shopeasy.com",
    "sub": "order-service",           // Subject is the SERVICE, not a user
    "aud": "shopeasy-api",
    "client_id": "order-service",
    "scope": "inventory.read payment.charge",
    "exp": 1735690500,                // Short-lived (15 min)
    "iat": 1735689600
    // NOTE: No "name", "email", "role" — there's no human here
  }
}
```

---

## Part 7: How Each Service Makes Authorization Decisions

```csharp
// === Product Service — Controller Example ===

[ApiController]
[Route("api/products")]
public class ProductsController : ControllerBase
{
    // Anyone with a valid token can browse products
    [Authorize]
    [HttpGet]
    public IActionResult GetAll()
    {
        return Ok(_productRepo.GetAll());
    }

    // Only admins can create products
    [Authorize(Roles = "admin")]
    [HttpPost]
    public IActionResult Create(ProductDto dto)
    {
        // The "role" claim in the token is checked automatically
        // by the [Authorize(Roles = "admin")] attribute.
        // If role != "admin", the framework returns 403 Forbidden.
        return Ok(_productRepo.Create(dto));
    }

    // Only the product owner can update their listing
    [Authorize]
    [HttpPut("{id}")]
    public IActionResult Update(int id, ProductDto dto)
    {
        var userId = User.FindFirst("sub")?.Value;  // Extract user ID from token
        var product = _productRepo.GetById(id);

        if (product.OwnerId != userId)
            return Forbid();  // 403 — valid token, but not your product

        return Ok(_productRepo.Update(id, dto));
    }
}
```

```csharp
// === Inventory Service — Accepting both user tokens AND service tokens ===

[ApiController]
[Route("api/stock")]
public class StockController : ControllerBase
{
    [Authorize]
    [HttpGet("{productId}")]
    public IActionResult CheckStock(string productId)
    {
        // This endpoint might be called by:
        // 1. The Gateway (forwarding a user's token)  → has "sub" claim
        // 2. The Order Service (its own service token) → has "client_id" claim

        // Both are valid — the [Authorize] attribute just checks
        // that the token is properly signed and not expired.

        // If you need to distinguish:
        var isServiceCall = User.FindFirst("client_id")?.Value == "order-service"
                            && User.FindFirst("sub") == null;

        return Ok(_stockRepo.GetStock(productId));
    }
}
```

---

## Part 8: Token Validation — What Actually Happens

When a microservice receives a request with `Authorization: Bearer eyJhb...`:

```
┌─────────────────────────────────────────────────────────────────┐
│                    TOKEN VALIDATION STEPS                        │
│            (performed by ASP.NET middleware automatically)       │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  Step 1: DECODE the JWT (it's just base64, not encrypted)        │
│          → Extract header, payload, signature                    │
│                                                                  │
│  Step 2: FIND THE KEY                                            │
│          → Read "kid" (key ID) from the header                   │
│          → Look up matching public key from the cached JWKS      │
│            (fetched from IdP's /.well-known/jwks endpoint        │
│             on startup, refreshed periodically)                  │
│                                                                  │
│  Step 3: VERIFY SIGNATURE                                        │
│          → Use the public key to verify the token's signature    │
│          → If it doesn't match, REJECT (401 Unauthorized)        │
│          → This proves the IdP issued this token and it hasn't   │
│            been tampered with                                    │
│                                                                  │
│  Step 4: CHECK EXPIRATION                                        │
│          → Is current time > "exp" claim?                        │
│          → If yes, REJECT (401 Unauthorized)                     │
│                                                                  │
│  Step 5: CHECK AUDIENCE                                          │
│          → Does "aud" claim match this service's expected         │
│            audience ("shopeasy-api")?                             │
│          → If not, REJECT (401 Unauthorized)                     │
│          → This prevents a token meant for another system        │
│            from being used here                                  │
│                                                                  │
│  Step 6: CHECK ISSUER                                            │
│          → Does "iss" claim match the expected IdP URL?           │
│          → If not, REJECT (401 Unauthorized)                     │
│          → This prevents tokens from a rogue IdP being accepted  │
│                                                                  │
│  Step 7: POPULATE USER CLAIMS                                    │
│          → All claims from the token payload are loaded into     │
│            the HttpContext.User object                            │
│          → Controller code can now access User.FindFirst("sub"), │
│            User.IsInRole("admin"), etc.                          │
│                                                                  │
│  ═══════════════════════════════════════════════════════════════  │
│  IMPORTANT: None of these steps require calling the IdP.         │
│  The service validates the token LOCALLY using cached public     │
│  keys. This is why JWT-based auth scales well — no network       │
│  calls needed for validation.                                    │
│                                                                  │
│  The only exception: Token Revocation. If a user is banned or   │
│  logs out, the JWT is still technically valid until it expires.  │
│  This is why Access Tokens are short-lived (minutes to 1 hour). │
│  For immediate revocation, you'd need a token blacklist (Redis) │
│  or switch to Reference Tokens (opaque tokens that require an   │
│  IdP lookup on every request — slower but revocable).            │
└─────────────────────────────────────────────────────────────────┘
```

---

## Part 9: Complete Picture — All Flows Together

```
                              ┌─────────────────┐
                              │       IdP        │
                              │ (Duende Identity │
                              │    Server /      │
                              │  Azure AD /      │
                              │    Auth0 /       │
                              │   Keycloak)      │
                              │                  │
                              │  • Login Page    │
                              │  • Token Endpoint│
                              │  • Public Keys   │
                              │  • User Store    │
                              │  • Client Reg.   │
                              └────────┬─────────┘
                                       │
                    ┌──────────────────┼──────────────────┐
                    │                  │                  │
              (auth code          (auth code        (client cred.
               exchange)          + PKCE)            grant)
                    │                  │                  │
                    ▼                  │                  │
┌────────┐   ┌──────────┐             │            ┌──────────────┐
│ React  │──>│  Gateway  │             │            │ Order Service│
│  SPA   │   │ (BFF +    │             │            │ (calls other │
│        │   │  Proxy)   │             │            │  services as │
│  No    │   │           │             │            │  itself when │
│ tokens │   │ Holds     │             │            │  no user     │
│ just   │   │ tokens    │             │            │  context)    │
│ cookie │   │ in cookie │             │            └──────┬───────┘
└────────┘   └─────┬─────┘             │                   │
                   │                   │                   │
                   │            ┌──────┴──────┐            │
                   │            │ Mobile App  │            │
                   │            │             │            │
                   │            │ Holds tokens│            │
                   │            │ in Keychain/│            │
                   │            │ Keystore    │            │
                   │            │             │            │
                   │            │ Attaches    │            │
                   │            │ Bearer token│            │
                   │            │ to requests │            │
                   │            └──────┬──────┘            │
                   │                   │                   │
                   ▼                   ▼                   ▼
          ┌──────────────────────────────────────────────────────┐
          │              MICROSERVICES                           │
          │                                                      │
          │  ┌─────────┐  ┌─────────┐  ┌───────────┐  ┌───────┐│
          │  │ Product │  │  Order  │  │ Inventory │  │Payment││
          │  │ Service │  │ Service │  │  Service  │  │Service││
          │  │         │  │         │  │           │  │       ││
          │  │ Validate│  │ Validate│  │ Validate  │  │Validat││
          │  │ tokens  │  │ tokens  │  │ tokens    │  │tokens ││
          │  │ using   │  │ using   │  │ using     │  │using  ││
          │  │ public  │  │ public  │  │ public    │  │public ││
          │  │ key     │  │ key     │  │ key       │  │key    ││
          │  └─────────┘  └────┬────┘  └───────────┘  └───────┘│
          │                    │                                 │
          │                    │ (service-to-service:            │
          │                    │  forwards user token            │
          │                    │  OR uses own client             │
          │                    │  credentials token)             │
          │                    │                                 │
          │              ┌─────┴──────────┐                     │
          │              │ Message Broker │                     │
          │              │ (RabbitMQ /    │                     │
          │              │  Kafka /       │                     │
          │              │  Azure SB)     │                     │
          │              │                │                     │
          │              │ No OAuth here  │                     │
          │              │ Broker auth    │                     │
          │              │ (conn strings/ │                     │
          │              │  certificates) │                     │
          │              └────────────────┘                     │
          └──────────────────────────────────────────────────────┘
```

---

## Quick Reference: Who Has What

| Component | Client ID? | Client Secret? | Holds User Tokens? | Validates Tokens? |
|-----------|-----------|---------------|--------------------|--------------------|
| React SPA | No | No | No (cookie only) | No |
| Mobile App | Yes | No (uses PKCE) | Yes (in Keychain) | No (Gateway does it) |
| API Gateway | Yes | Yes | Yes (in cookie) | Yes |
| Product Service | No | No | No | Yes (via public key) |
| Order Service | Yes (for Client Credentials) | Yes | Receives from Gateway | Yes (via public key) |
| Inventory Service | No | No | No | Yes (via public key) |
| Payment Service | No | No | No | Yes (via public key) |
| Message Broker | N/A | N/A | N/A | N/A (own auth) |
| IdP | N/A | N/A | Issues them | N/A |
