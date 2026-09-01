namespace HgVs.Models;

public class HgMergeBaseCandidate
{
    public string Revision { get; set; }
    public string Branch { get; set; }
    public string Description { get; set; }
    public string Date { get; set; }
    public int RevisionNumber { get; set; }
    public int MergeRevisionNumber { get; set; }
    public bool IsLatestDefault { get; set; }
    public string RevisionLabel => "r" + RevisionNumber;
    public string MergeLabel => "merged at r" + MergeRevisionNumber;
}
