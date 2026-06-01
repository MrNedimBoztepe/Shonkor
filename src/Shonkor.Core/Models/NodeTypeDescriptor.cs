namespace Shonkor.Core.Models;

public record NodeTypeDescriptor(
    string TypeName,           // e.g. "Class", "DockerStage", "SitecoreItem"
    string Category,           // "Code", "Infrastructure", "CMS", "Documentation", "Interaction"
    bool IsVisibleByDefault    // true = immediately visible, false = needs to be activated
);
