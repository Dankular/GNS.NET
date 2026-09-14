using GnsNet;

// Implement this boundary with a backend-issued, signed, short-lived token.
// Do not use --insecure outside local development.
ConnectionAdmission CreateAdmission(IReadOnlyDictionary<string, byte[]> keys)
    => new(new JwksTokenValidator(keys));
