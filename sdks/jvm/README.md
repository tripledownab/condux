# Condux SDK for the JVM

Report errors from a Java, Kotlin, or Scala app to a Condux relay. Emits the Sentry "store" wire shape,
so the relay normalizes it exactly like an official Sentry SDK — point it at a project DSN and it works.

Delivery is resilient (429 / 5xx / network failures retry with backoff, honoring `Retry-After`) and
**never throws** — a failed send returns a `SendResult`, it does not crash the caller.

## Install

Maven:

```xml
<dependency>
  <groupId>ai.condux</groupId>
  <artifactId>condux</artifactId>
  <version>0.1.0</version>
</dependency>
```

Gradle:

```kotlin
implementation("ai.condux:condux:0.1.0")
```

## Usage

```java
import ai.condux.ConduxClient;
import ai.condux.Level;

ConduxClient condux = ConduxClient.builder("https://<key>@ingest.condux.ai/<projectId>")
    .environment("production")
    .release("1.4.2")
    .build();

try {
    doWork();
} catch (Exception e) {
    condux.captureException(e); // captures the exception's stack trace
    throw e;
}

// or a bare message
condux.captureMessage("cache miss storm", Level.WARNING);
```

### Which frames are yours

Condux marks each stack frame as the application's or not, and that decides how issues group, which
frame is shown as the culprit, and which files an automated fix reads. By default it excludes the JDK,
this SDK, and the layers that wrap every web request (Spring, Tomcat, Jetty, Undertow), which is right
for an ordinary application.

Set `inAppPackages` if your code sits somewhere that guess cannot reach, such as inside a framework's
own namespace, or if an internal shared library should count as yours. It **replaces** the default:
what you list is in-app, and nothing else is.

```java
ConduxClient.builder(dsn)
    .inAppPackages("com.acme.", "com.acme.platform.")
    .build();
```

### Spring Boot / servlet apps

`ai.condux.servlet.ConduxExceptionFilter` is a servlet `Filter` that reports an uncaught request
exception as **unhandled** and re-throws, so the app's own error handling still runs. Register it as a
bean (Spring Boot MVC, or any Jakarta servlet app):

```java
@Bean
FilterRegistrationBean<ConduxExceptionFilter> conduxFilter(ConduxClient client) {
    var registration = new FilterRegistrationBean<>(new ConduxExceptionFilter(client));
    registration.setOrder(Ordered.HIGHEST_PRECEDENCE);
    return registration;
}
```

**On Spring MVC, register the resolver as well.** The filter alone only sees exceptions that escape the
servlet, and `DispatcherServlet` converts anything a `HandlerExceptionResolver` claims into a response
first. So an app with a global `@ExceptionHandler(Exception.class)`, which many have, would report
nothing at all through the filter:

```java
@Bean
ConduxExceptionResolver conduxExceptionResolver(ConduxClient client) {
    return new ConduxExceptionResolver(client);
}
```

It runs before the resolvers that would claim the exception and **never claims it itself**, so your own
error handling is untouched and responses are unchanged. Registering both is correct: they cover
different halves and will not double report.

Its only dependency is the servlet API (`jakarta.servlet-api`, `provided` scope), which the container
supplies at runtime, so the SDK stays runtime dependency-free; the resolver additionally needs Spring,
which is `provided` and `optional` for the same reason. The filter refuses to be constructed without a
client, so a missing registration fails at startup rather than on every request.

## Verify your setup

Silence is what a broken error monitor and a healthy app look like from the outside, so prove the
pipeline once:

```bash
CONDUX_DSN="https://<key>@ingest.condux.ai/<projectId>" java -cp condux.jar ai.condux.TestEvent
```

Exit code 0 means delivered (the message appears as an info-level issue), 1 means delivery failed and
prints why, 2 means the DSN was missing or malformed.

## Enrichment

Attach the ambient facts triage always needs. Every subsequent event carries them, so nothing has to be
threaded through capture calls:

```java
import ai.condux.ConduxScope;

ConduxScope.setUser(Map.of("id", "1042", "email", "dev@example.com")); // null clears it (sign-out)
ConduxScope.setTag("plan", "team");                                    // null removes the tag
ConduxScope.setContext("job", Map.of("queue", "billing"));             // null removes the context
ConduxScope.addBreadcrumb(ConduxScope.Breadcrumb.of("charge.started").category("billing"));
```

The trail keeps the most recent `ConduxScope.MAX_BREADCRUMBS` (30) entries. `ConduxScope.clear()` resets
everything.

### In a server, scope one request at a time

Those calls are **process wide** by default, which is right for facts about the deployment and wrong for
facts about one request: a servlet container serves requests concurrently, so a bare `setUser` in a
controller can attach that user to a different request's error. That is worse than reporting no user,
because it is confidently wrong.

`ConduxScope.beginRequest()` isolates it. Anything set inside belongs to that request alone, layered
over the process-wide values:

```java
try (var scope = ConduxScope.beginRequest()) {
    ConduxScope.setUser(Map.of("id", userId)); // this request only
    handle(request);
}
```

**`ConduxExceptionFilter` does this for you**, so a `setUser` in a Spring controller is already isolated.
Call it directly around a background job, which has the same problem of many in flight at once. It uses a
`ThreadLocal` and removes it on close, because containers pool request threads and a value left behind is
handed to the next request that worker picks up.

Detail belonging to a single event can skip the scope entirely:

```java
client.captureException(error, false, CaptureContext.of()
        .request("/checkout", "POST", "step=2")
        .tag("route", "/checkout/{id}"));
```

## Develop

The SDK is runtime dependency-free, so it builds and tests with just a JDK (no build tool required); the
servlet adapter compiles against the `provided` servlet API, fetched as a single jar:

```bash
curl -sfL -o /tmp/servlet-api.jar https://repo1.maven.org/maven2/jakarta/servlet/jakarta.servlet-api/6.0.0/jakarta.servlet-api-6.0.0.jar
javac -cp /tmp/servlet-api.jar -d out $(find src/main/java -name '*.java')
javac -cp out:/tmp/servlet-api.jar -d out-test $(find src/test/java -name '*.java')
java -cp out:out-test ai.condux.ConduxClientTest
java -cp out:out-test ai.condux.ConduxScopeTest
java -cp out:out-test ai.condux.TestEventTest
java -cp out:out-test:/tmp/servlet-api.jar ai.condux.servlet.ConduxExceptionFilterTest
```

HTTP is `java.net.http.HttpClient` and the JSON is written by hand — no third-party runtime
dependencies. The transport, sleep, and clock are injectable via the builder
(`.transport(...)`, `.sleeper(...)`, `.clock(...)`), so the tests exercise the retry/backoff with no
real network or timers. `pom.xml` is the Maven Central publish descriptor.
