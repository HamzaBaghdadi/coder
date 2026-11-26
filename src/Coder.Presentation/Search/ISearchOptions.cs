using System.ComponentModel;

namespace Coder.Presentation.Search;

/// <summary>The match options shared by find-in-file and search-in-files, so one UI can drive both.</summary>
public interface ISearchOptions : INotifyPropertyChanged
{
    bool MatchCase { get; set; }

    bool WholeWord { get; set; }

    bool UseRegex { get; set; }
}
