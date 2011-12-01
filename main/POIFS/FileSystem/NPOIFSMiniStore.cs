
/* ====================================================================
   Licensed to the Apache Software Foundation (ASF) under one or more
   contributor license agreements.  See the NOTICE file distributed with
   this work for Additional information regarding copyright ownership.
   The ASF licenses this file to You under the Apache License, Version 2.0
   (the "License"); you may not use this file except in compliance with
   the License.  You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
==================================================================== */


using NPOI.POIFS.Common;
using NPOI.POIFS.Storage;
namespace NPOI.POIFS.FileSystem
{

    /**
     * This class handles the MiniStream (small block store)
     *  in the NIO case for {@link NPOIFSFileSystem}
     */
    public class NPOIFSMiniStore : BlockStore
    {
        private NPOIFSFileSystem _filesystem;
        private NPOIFSStream _mini_stream;
        private List<BATBlock> _sbat_blocks;
        private HeaderBlock _header;
        private RootProperty _root;

        protected NPOIFSMiniStore(NPOIFSFileSystem filesystem, RootProperty root,
             List<BATBlock> sbats, HeaderBlock header)
        {
            this._filesystem = filesystem;
            this._sbat_blocks = sbats;
            this._header = header;
            this._root = root;

            this._mini_stream = new NPOIFSStream(filesystem, root.GetStartBlock());
        }

        /**
         * Load the block at the given offset.
         */
        protected ByteBuffer GetBlockAt(int offset)
        {
            // Which big block is this?
            int byteOffset = offset * POIFSConstants.SMALL_BLOCK_SIZE;
            int bigBlockNumber = byteOffset / _filesystem.GetBigBlockSize();
            int bigBlockOffset = byteOffset % _filesystem.GetBigBlockSize();

            // Now locate the data block for it
            Iterator<ByteBuffer> it = _mini_stream.GetBlockIterator();
            for (int i = 0; i < bigBlockNumber; i++)
            {
                it.next();
            }
            ByteBuffer dataBlock = it.next();
            if (dataBlock == null)
            {
                throw new IndexOutOfBoundsException("Big block " + bigBlockNumber + " outside stream");
            }

            // Position ourselves, and take a slice 
            dataBlock.position(
                  dataBlock.position() + bigBlockOffset
            );
            ByteBuffer miniBuffer = dataBlock.slice();
            miniBuffer.limit(POIFSConstants.SMALL_BLOCK_SIZE);
            return miniBuffer;
        }

        /**
         * Load the block, extending the underlying stream if needed
         */
        protected ByteBuffer CreateBlockIfNeeded(int offset)
        {
            // Try to Get it without extending the stream
            try
            {
                return GetBlockAt(offset);
            }
            catch (IndexOutOfBoundsException e)
            {
                // Need to extend the stream
                // TODO Replace this with proper append support
                // For now, do the extending by hand...

                // Ask for another block
                int newBigBlock = _filesystem.GetFreeBlock();
                _filesystem.CreateBlockIfNeeded(newBigBlock);

                // Tack it onto the end of our chain
                ChainLoopDetector loopDetector = _filesystem.GetChainLoopDetector();
                int block = _mini_stream.GetStartBlock();
                while (true)
                {
                    loopDetector.claim(block);
                    int next = _filesystem.GetNextBlock(block);
                    if (next == POIFSConstants.END_OF_CHAIN)
                    {
                        break;
                    }
                    block = next;
                }
                _filesystem.SetNextBlock(block, newBigBlock);
                _filesystem.SetNextBlock(newBigBlock, POIFSConstants.END_OF_CHAIN);

                // Now try again to Get it
                return CreateBlockIfNeeded(offset);
            }
        }

        /**
         * Returns the BATBlock that handles the specified offset,
         *  and the relative index within it
         */
        protected BATBlockAndIndex GetBATBlockAndIndex(int offset)
        {
            return BATBlock.GetSBATBlockAndIndex(
                  offset, _header, _sbat_blocks
            );
        }

        /**
         * Works out what block follows the specified one.
         */
        protected int GetNextBlock(int offset)
        {
            BATBlockAndIndex bai = GetBATBlockAndIndex(offset);
            return bai.GetBlock().GetValueAt(bai.GetIndex());
        }

        /**
         * Changes the record of what block follows the specified one.
         */
        protected void SetNextBlock(int offset, int nextBlock)
        {
            BATBlockAndIndex bai = GetBATBlockAndIndex(offset);
            bai.GetBlock().SetValueAt(
                  bai.GetIndex(), nextBlock
            );
        }

        /**
         * Finds a free block, and returns its offset.
         * This method will extend the file if needed, and if doing
         *  so, allocate new FAT blocks to Address the extra space.
         */
        protected int GetFreeBlock()
        {
            int sectorsPerSBAT = _filesystem.GetBigBlockSizeDetails().GetBATEntriesPerBlock();

            // First up, do we have any spare ones?
            int offset = 0;
            for (int i = 0; i < _sbat_blocks.Count; i++)
            {
                // Check this one
                BATBlock sbat = _sbat_blocks.Get(i);
                if (sbat.HasFreeSectors())
                {
                    // Claim one of them and return it
                    for (int j = 0; j < sectorsPerSBAT; j++)
                    {
                        int sbatValue = sbat.GetValueAt(j);
                        if (sbatValue == POIFSConstants.UNUSED_BLOCK)
                        {
                            // Bingo
                            return offset + j;
                        }
                    }
                }

                // Move onto the next SBAT
                offset += sectorsPerSBAT;
            }

            // If we Get here, then there aren't any
            //  free sectors in any of the SBATs
            // So, we need to extend the chain and add another

            // Create a new BATBlock
            BATBlock newSBAT = BATBlock.CreateEmptyBATBlock(_filesystem.GetBigBlockSizeDetails(), false);
            int batForSBAT = _filesystem.GetFreeBlock();
            newSBAT.SetOurBlockIndex(batForSBAT);

            // Are we the first SBAT?
            if (_header.GetSBATCount() == 0)
            {
                _header.SetSBATStart(batForSBAT);
                _header.SetSBATBlockCount(1);
            }
            else
            {
                // Find the end of the SBAT stream, and add the sbat in there
                ChainLoopDetector loopDetector = _filesystem.GetChainLoopDetector();
                int batOffset = _header.GetSBATStart();
                while (true)
                {
                    loopDetector.claim(batOffset);
                    int nextBat = _filesystem.GetNextBlock(batOffset);
                    if (nextBat == POIFSConstants.END_OF_CHAIN)
                    {
                        break;
                    }
                    batOffset = nextBat;
                }

                // Add it in at the end
                _filesystem.SetNextBlock(batOffset, batForSBAT);

                // And update the count
                _header.SetSBATBlockCount(
                      _header.GetSBATCount() + 1
                );
            }

            // Finish allocating
            _filesystem.SetNextBlock(batForSBAT, POIFSConstants.END_OF_CHAIN);
            _sbat_blocks.Add(newSBAT);

            // Return our first spot
            return offset;
        }


        protected ChainLoopDetector GetChainLoopDetector()
        {
            return new ChainLoopDetector(_root.GetSize());
        }

        protected int GetBlockStoreBlockSize()
        {
            return POIFSConstants.SMALL_BLOCK_SIZE;
        }

        /**
         * Writes the SBATs to their backing blocks
         */
        protected void syncWithDataSource()
        {
            foreach (BATBlock sbat in _sbat_blocks)
            {
                ByteBuffer block = _filesystem.GetBlockAt(sbat.GetOurBlockIndex());
                BlockAllocationTableWriter.WriteBlock(sbat, block);
            }
        }
    }
}






