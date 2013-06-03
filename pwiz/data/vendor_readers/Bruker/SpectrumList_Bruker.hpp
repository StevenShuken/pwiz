//
// $Id$
//
//
// Original author: Matt Chambers <matt.chambers .@. vanderbilt.edu>
//
// Copyright 2009 Vanderbilt University - Nashville, TN 37232
//
// Licensed under the Apache License, Version 2.0 (the "License"); 
// you may not use this file except in compliance with the License. 
// You may obtain a copy of the License at 
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software 
// distributed under the License is distributed on an "AS IS" BASIS, 
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. 
// See the License for the specific language governing permissions and 
// limitations under the License.
//


#ifndef _SPECTRUMLIST_BRUKER_HPP_
#define _SPECTRUMLIST_BRUKER_HPP_

#include "pwiz/utility/misc/Export.hpp"
#include "pwiz/data/msdata/SpectrumListBase.hpp"
#include "pwiz/utility/misc/IntegerSet.hpp"
#include "pwiz/utility/misc/Filesystem.hpp"
#include "pwiz/utility/misc/Container.hpp"
#include "pwiz/utility/misc/String.hpp"
#include "pwiz/utility/misc/Stream.hpp"
#include "Reader_Bruker_Detail.hpp"


namespace pwiz {
namespace msdata {
namespace detail {

using boost::shared_ptr;

//
// SpectrumList_Bruker
//
class PWIZ_API_DECL SpectrumList_Bruker : public SpectrumListBase
{
    public:

    virtual size_t size() const;
    virtual const SpectrumIdentity& spectrumIdentity(size_t index) const;
    virtual size_t find(const string& id) const;
    virtual SpectrumPtr spectrum(size_t index, bool getBinaryData) const;
    virtual SpectrumPtr spectrum(size_t index, DetailLevel detailLevel) const;
    virtual SpectrumPtr spectrum(size_t index, bool getBinaryData, const pwiz::util::IntegerSet& msLevelsToCentroid) const;
    virtual SpectrumPtr spectrum(size_t index, DetailLevel detailLevel, const pwiz::util::IntegerSet& msLevelsToCentroid) const;

#ifdef PWIZ_READER_BRUKER
    SpectrumList_Bruker(MSData& msd,
                        const string& rootpath,
                        Reader_Bruker_Format format,
                        CompassDataPtr compassDataPtr);

    MSSpectrumPtr getMSSpectrumPtr(size_t index, vendor_api::Bruker::DetailLevel detailLevel) const;

    private:

    struct ParameterCache
    {
        string get(const string& parameterName, MSSpectrumParameterList& parameters);
        size_t size() { return parameterIndexByName_.size(); }

        private:
        void update(MSSpectrumParameterList& parameters);
        map<string, size_t> parameterIndexByName_;
        map<string, string> parameterAlternativeNameMap_;
    };

    MSData& msd_;
    bfs::path rootpath_;
    Reader_Bruker_Format format_;
    mutable CompassDataPtr compassDataPtr_;
    mutable map<int, ParameterCache> parameterCacheByMsLevel_;
    size_t size_;
    vector<bfs::path> sourcePaths_;

    struct IndexEntry : public SpectrumIdentity
    {
        int source;
        int collection; // -1 for an MS spectrum
        int scan;
    };

    vector<IndexEntry> index_;

    // idToIndexMap_["scan=<#>" or "file=<sourceFile::id>"] == index
    map<string, size_t> idToIndexMap_;

    void fillSourceList();
    void createIndex();
    //string findPrecursorID(int precursorMsLevel, size_t index) const;
#endif // PWIZ_READER_BRUKER
};

} // detail
} // msdata
} // pwiz

#endif // _SPECTRUMLIST_BRUKER_HPP_
