package ai.condux;

import java.net.URI;

/** A parsed DSN: the relay endpoint, the project id path segment, and the public key. */
record Dsn(String endpoint, String projectId, String publicKey) {
    static Dsn parse(String dsn) {
        URI uri;
        try {
            uri = URI.create(dsn);
        } catch (IllegalArgumentException e) {
            throw new IllegalArgumentException("Condux: DSN must be scheme://<key>@<host>/<projectId>", e);
        }
        String userInfo = uri.getUserInfo();
        if (userInfo == null || userInfo.isEmpty() || uri.getHost() == null || uri.getScheme() == null) {
            throw new IllegalArgumentException("Condux: DSN must be scheme://<key>@<host>/<projectId>");
        }
        String publicKey = userInfo.contains(":") ? userInfo.substring(0, userInfo.indexOf(':')) : userInfo;
        String endpoint = uri.getScheme() + "://" + uri.getHost() + (uri.getPort() != -1 ? ":" + uri.getPort() : "");
        String path = uri.getPath() == null ? "" : uri.getPath();
        String projectId = path.startsWith("/") ? path.substring(1) : path;
        if (projectId.isEmpty()) {
            throw new IllegalArgumentException("Condux: DSN is missing the project id path segment");
        }
        return new Dsn(endpoint, projectId, publicKey);
    }

    /** The Sentry store endpoint an event is POSTed to. */
    String storeUrl() {
        return endpoint + "/api/" + projectId + "/store/";
    }
}
