namespace ApiCollection.Core.Models;

public enum HttpMethodKind
{
    Get,
    Post,
    Put,
    Patch,
    Delete,
    Head,
    Options
}

public enum ParameterLocation
{
    Path,
    Query,
    Header,
    Body
}
