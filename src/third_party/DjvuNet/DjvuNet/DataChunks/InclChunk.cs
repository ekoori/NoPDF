// <copyright file="InclChunk.cs" company="">
// TODO: Update copyright text.
// </copyright>

using System;
using System.Text;

namespace DjvuNet.DataChunks
{

    /// <summary>
    /// TODO: Update summary.
    /// </summary>
    public class InclChunk : DjvuNode, IInclChunk
    {
        #region Public Properties

        #region ChunkType

        public override ChunkType ChunkType
        {
            get { return ChunkType.Incl; }
        }

        #endregion ChunkType

        #region IncludeID

        private string _IncludeID;

        /// <summary>
        /// Gets the ID of the element to include
        /// </summary>
        public string IncludeID
        {
            get
            {
                if (_IncludeID != null)
                {
                    return _IncludeID;
                }
                else
                {
                    ReadData(Reader);
                    if (_IncludeID == null)
                        _IncludeID = String.Empty;
                    return _IncludeID;
                }
            }
            set
            {
                _IncludeID = value;
            }
        }

        #endregion IncludeID

        #endregion Public Properties

        #region Constructors

        public InclChunk(IDjvuReader reader, IDjvuElement parent, IDjvuDocument document,
            string chunkID = "", long length = 0)
            : base(reader, parent, document, chunkID, length)
        {
        }

        public InclChunk(IDjvuWriter writer, IDjvuElement parent, long length = 0)
            : base(writer, parent, length)
        {
        }

        #endregion Constructors

        #region Methods

        public override void ReadData(IDjvuReader reader)
        {
            long prevPos = reader.Position;
            reader.Position = DataOffset;

            // Safely consume the exact payload length to maintain stream alignment
            byte[] buffer = reader.ReadBytes((int)Length);

            // Byte-walking to find actual string boundaries
            int start = 0;
            int end = buffer.Length - 1;
            while (start <= end && (buffer[start] == '\n' || buffer[start] == '\r' || buffer[start] == 0)) start++;
            while (end >= start && (buffer[end] == '\n' || buffer[end] == '\r' || buffer[end] == 0)) end--;

            if (start <= end)
                IncludeID = Encoding.UTF8.GetString(buffer, start, end - start + 1);
            else
                IncludeID = string.Empty;

            reader.Position = prevPos;
        }

        #endregion Methods
    }
}
