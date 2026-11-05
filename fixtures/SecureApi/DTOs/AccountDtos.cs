namespace SecureApiFixture.DTOs;

public sealed class CreateAccountRequest
{
    /// <example>Ada Example</example>
    public required string DisplayName { get; init; }

    /// <example>ada@example.com</example>
    public required string Email { get; init; }

    public AddressDto? Address { get; init; }
}

public sealed class AddressDto
{
    /// <example>Berlin</example>
    public string City { get; init; } = "";

    public string Street { get; init; } = "";
}
