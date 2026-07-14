namespace FileCopyExtension.Models;

public class HgFileItem
{
    public string Status { get; set; }
    public string Path { get; set; }
    public bool IsUnchanged { get; set; }
}