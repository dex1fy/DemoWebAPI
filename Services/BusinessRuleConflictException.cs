namespace DemoWebAPI.Services;

public sealed class BusinessRuleConflictException(string message) : Exception(message);
