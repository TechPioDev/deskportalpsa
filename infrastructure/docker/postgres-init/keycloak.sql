-- Keycloak's own database, owned by the same role the portal uses. Runs only on first init;
-- an existing server creates it by hand: CREATE DATABASE keycloak OWNER <POSTGRES_USER>;
CREATE DATABASE keycloak;
