// <copyright file="DirmComponent.cs" company="">
// TODO: Update copyright text.
// </copyright>

using System;

namespace DjvuNet.DataChunks
{
    /// <summary>
    /// TODO: Update summary.
    /// </summary>
    public class DirmComponent
    {
        #region Public Properties

        #region ID

        /// <summary>
        /// Gets or sets the ID of the component
        /// </summary>
        public string ID { get; set; }

        #endregion ID

        #region Name

        private string _name;

        /// <summary>
        /// Gets or sets the name of the component
        /// </summary>
        public string Name
        {
            get => string.IsNullOrEmpty(_name) ? ID : _name;
            set => _name = value;
        }

        #endregion Name

        #region Title

        private string _title;

        /// <summary>
        /// Gets or sets the title of the component
        /// </summary>
        public string Title
        {
            get => string.IsNullOrEmpty(_title) ? ID : _title;
            set => _title = value;
        }

        #endregion Title

        #region Offset

        /// <summary>
        /// Gets the offset of the component
        /// </summary>
        public int Offset { get; set; }

        #endregion Offset

        #region Size

        /// <summary>
        /// Gets or sets the size of the component
        /// </summary>
        public int Size { get; set; }

        #endregion Size

        #region HasName

        /// <summary>
        /// True if the component has a different name
        /// </summary>
        public bool HasName { get; internal set; }

        #endregion HasName

        #region HasTitle

        /// <summary>
        /// True if the component has a different title
        /// </summary>
        public bool HasTitle { get; internal set; }

        #endregion HasTitle

        #region IsIncluded

        /// <summary>
        /// True if the component is included by other files
        /// </summary>
        public bool IsIncluded { get; set; }

        #endregion IsIncluded

        #region IsPage

        /// <summary>
        /// True if the component represents a page
        /// </summary>
        public bool IsPage { get; internal set; }

        #endregion IsPage

        #region IsThumbnail

        /// <summary>
        /// True if the component represents a thumbnail
        /// </summary>
        public bool IsThumbnail { get; internal set; }

        #endregion IsThumbnail

        #region IsSharedAnno

        /// <summary>
        /// True if the component represents a shared annotations file
        /// </summary>
        public bool IsSharedAnno { get; internal set; }

        #endregion IsSharedAnno

        #endregion Public Properties

        #region Constructors

        public DirmComponent(int offset)
        {
            Offset = offset;
        }

        #endregion Constructors

        #region Public Methods

        /// <summary>
        /// Decode component flags.
        /// </summary>
        /// <param name="flag"></param>
        /// <param name="version">The DIRM chunk version</param>
        public void DecodeFlags(byte flag, int version)
        {
            if (version == 0)
            {
                HasName = (flag & 0x02) == 0x02;
                HasTitle = (flag & 0x04) == 0x04;
                IsPage = (flag & 0x01) == 0x01;
                IsIncluded = !IsPage;
                IsThumbnail = false;
                IsSharedAnno = false;
            }
            else
            {
                HasName = (flag & 0x80) == 0x80;
                HasTitle = (flag & 0x40) == 0x40;

                int test = flag & 0x3f;

                IsIncluded = (test == 0);
                IsPage = (test == 1);
                IsThumbnail = (test == 2);
                IsSharedAnno = (test == 3);
            }
        }

        public override string ToString()
        {
            return  $"{ID}";
        }

        #endregion Public Methods
    }
}
