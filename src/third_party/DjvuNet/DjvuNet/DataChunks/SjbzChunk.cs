// <copyright file="SjbzChunk.cs" company="">
// TODO: Update copyright text.
// </copyright>

using System.Collections.Generic;
using System.Linq;
using DjvuNet.JB2;

namespace DjvuNet.DataChunks
{

    /// <summary>
    /// TODO: Update summary.
    /// </summary>
    public class SjbzChunk : DjvuNode, ISjbzChunk
    {

        #region Public Properties

        #region ChunkType

        public override ChunkType ChunkType
        {
            get { return ChunkType.Sjbz; }
        }

        #endregion ChunkType

        #region Image

        private JB2Image _image;

        /// <summary>
        /// Gets the image this chunk represents
        /// </summary>
        public JB2Image Image
        {
            get
            {
                if (_image == null)
                {
                    _image = ReadCompressedImage();
                }

                return _image;
            }

            private set
            {
                if (_image != value)
                {
                    _image = value;
                }
            }
        }

        #endregion Image

        #endregion Public Properties

        #region Constructors

        public SjbzChunk(IDjvuReader reader, IDjvuElement parent, IDjvuDocument document,
            string chunkID = "", long length = 0)
            : base(reader, parent, document, chunkID, length)
        {
        }

        public SjbzChunk(IDjvuWriter writer, IDjvuElement parent, long length = 0)
            : base(writer, parent, length)
        {
        }

        #endregion Constructors

        #region Methods

        internal JB2Image ReadCompressedImage()
        {
            using (IDjvuReader reader = Reader.CloneReaderToMemory(DataOffset, Length))
            {
                JB2Image image = new JB2Image();
                JB2.JB2Dictionary includedDictionary = null;

                if (Parent is DjvuChunk djvuChunk)
                {
                    IReadOnlyList<InclChunk> includes = djvuChunk.IncludedItems;
                    string targetID = null;

                    if (includes?.Count > 0)
                    {
                        var includeIDs = includes
                            .Where<InclChunk>(x => x.ChunkType == ChunkType.Incl);
                        var root = Document.RootForm as DjvmChunk;
                        DjbzChunk djbzItem = null;

                        IReadOnlyList<DirmComponent> components = root?.Dirm?.Components;
                        IReadOnlyList<IDjviChunk> includeForms = root?.Includes;

                        foreach (InclChunk iChunk in includeIDs)
                        {
                            if (components == null) break;

                            targetID = iChunk.IncludeID;
                            DirmComponent component = null;

                            for (int i = 0; i < components.Count; i++)
                            {
                                DirmComponent c = components[i];
                                if (c.ID == targetID || c.Name == targetID || c.Title == targetID)
                                {
                                    component = c;
                                    break;
                                }
                            }

                            if (component != null && includeForms != null)
                            {
                                for (int i = 0; i < includeForms.Count; i++)
                                {
                                    IDjviChunk includeForm = includeForms[i];
                                    if (includeForm.DataOffset == (component.Offset + 12))
                                    {
                                        IReadOnlyList<IDjvuNode> children = includeForm.Children;
                                        if (children != null)
                                        {
                                            for (int j = 0; j < children.Count; j++)
                                            {
                                                if (children[j] is DjbzChunk djbz)
                                                {
                                                    djbzItem = djbz;
                                                    break;
                                                }
                                            }
                                        }
                                        break;
                                    }
                                }
                                if (djbzItem != null) break;
                            }
                        }

                        includedDictionary = djbzItem?.ShapeDictionary;
                    }
                }

                image.Decode(reader, includedDictionary);

                return image;
            }
        }

        #endregion Methods
    }
}
