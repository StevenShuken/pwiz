//
// cvgen.cpp
//
// 
// Original author: Darren Kessner <Darren.Kessner@cshs.org>
//
// Copyright 2007 Spielberg Family Center for Applied Proteomics
//   Cedars-Sinai Medical Center, Los Angeles, California  90048
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


#include "obo.hpp"
#include "boost/filesystem/path.hpp"
#include "boost/filesystem/fstream.hpp"
#include "boost/regex.hpp"
#include "pwiz/utility/misc/String.hpp"
#include <iostream>
#include <iomanip>
#include <fstream>
#include <iterator>
#include <algorithm>
#include <map>


using namespace std;
using namespace pwiz;
using namespace pwiz::msdata;
namespace bfs = boost::filesystem;


//
// This program selectively parses OBO format controlled vocabulary files 
// and generates C++ code (one hpp file and one cpp file).
//


void writeCopyright(ostream& os, const string& filename)
{
    os << "//\n"
       << "// " << filename << endl
       << "//\n"
          "//\n"
          "// Darren Kessner <Darren.Kessner@cshs.org>\n"
          "//\n"
          "// Copyright 2007 Spielberg Family Center for Applied Proteomics\n"
          "//   Cedars-Sinai Medical Center, Los Angeles, California  90048\n"
          "//\n"
          "// Licensed under the Apache License, Version 2.0 (the \"License\");\n"
          "// you may not use this file except in compliance with the License.\n"
          "// You may obtain a copy of the License at\n"
          "//\n"
          "// http://www.apache.org/licenses/LICENSE-2.0\n"
          "//\n"
          "// Unless required by applicable law or agreed to in writing, software\n"
          "// distributed under the License is distributed on an \"AS IS\" BASIS,\n"
          "// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.\n"
          "// See the License for the specific language governing permissions and\n"
          "// limitations under the License.\n"
          "//\n"
          "// This file was generated by cvgen.\n"
          "//\n\n\n";
}


string includeGuardString(const string& basename)
{
    string includeGuard = basename;
    transform(includeGuard.begin(), includeGuard.end(), includeGuard.begin(), (int(*)(int))toupper);
    return "_" + includeGuard + "_HPP_";
}


void namespaceBegin(ostream& os, const string& name)
{
    os << "namespace pwiz {\n\n\n";
}


void namespaceEnd(ostream& os, const string& name)
{
    os << "} // namespace pwiz\n\n\n";
}


inline char toAllowableChar(char a)
{
    return isalnum(a) ? a : '_';
}


string enumName(const string& prefix, const string& name)
{
    string result = name;
    transform(result.begin(), result.end(), result.begin(), toAllowableChar);
    result = prefix + "_" + result;
    return result;
}


string enumName(const Term& term)
{
    return enumName(term.prefix, term.name);
}


const size_t enumBlockSize_ = 100000000;


size_t enumValue(const Term& term, size_t index)
{
    return term.id + (enumBlockSize_ * index);
}


void writeHpp(const vector<OBO>& obos, const string& basename, const bfs::path& outputDir)
{
    string filename = basename + ".hpp";
    bfs::path filenameFullPath = outputDir / filename;
    bfs::ofstream os(filenameFullPath, ios::binary);

    writeCopyright(os, filename);

    string includeGuard = includeGuardString(basename);
    os << "#ifndef " << includeGuard << endl
       << "#define " << includeGuard << "\n\n\n"
       << "#include <string>\n"
       << "#include <vector>\n"
       << "#include \"pwiz/utility/misc/Export.hpp\"\n"
       << "\n\n";

    for (vector<OBO>::const_iterator obo=obos.begin(); obo!=obos.end(); ++obo)
    {
        os << "// [" << obo->filename << "]\n";
        
        for (vector<string>::const_iterator it=obo->header.begin(); it!=obo->header.end(); ++it)
            os << "//   " << *it << endl;

        os << "//\n";
    }
    os << "\n\n";

    namespaceBegin(os, basename);

    os << "/// enumeration of controlled vocabulary (CV) terms, generated from OBO file(s)\n" 
          "enum PWIZ_API_DECL CVID\n{\n"
          "    CVID_Unknown = -1";
    for (vector<OBO>::const_iterator obo=obos.begin(); obo!=obos.end(); ++obo)
    for (vector<Term>::const_iterator it=obo->terms.begin(); it!=obo->terms.end(); ++it)
    {
        os << ",\n\n"
           << "    /// " << it->name << ": " << it->def << "\n"
           << "    " << enumName(*it) << " = " << enumValue(*it, obo-obos.begin());
        
        if (obo->prefix == "MS") // add synonyms for PSI-MS only
        {
            for (vector<string>::const_iterator syn=it->exactSynonyms.begin(); 
                 syn!=it->exactSynonyms.end(); ++syn)
            {
                os << ",\n\n"
                   << "    /// " << it->name << ": " << it->def << "\n"
                   << "    " << enumName(it->prefix, *syn) << " = " << enumName(*it);
            }
        }
    }
    os << "\n}; // enum CVID\n\n\n"; 

    os << "/// Information about an ontology or CV source and a short 'lookup' tag to refer to.\n"
          "struct PWIZ_API_DECL CV\n"
          "{\n"
          "    /// the short label to be used as a reference tag with which to refer to this particular Controlled Vocabulary source description (e.g., from the cvLabel attribute, in CVParamType elements).\n"
          "    std::string id;\n"
          "\n"
          "    /// the URI for the resource.\n"
          "    std::string URI;\n"
          "\n"
          "    /// the usual name for the resource (e.g. The PSI-MS Controlled Vocabulary).\n"
          "    std::string fullName;\n"
          "\n"
          "    /// the version of the CV from which the referred-to terms are drawn.\n"
          "    std::string version;\n"
          "\n"
          "    /// returns true iff id, URI, fullName, and version are all pairwise equal\n"
          "    bool operator==(const CV& that) const;\n"
          "\n"
          "    /// returns ture iff id, URI, fullName, and version are all empty\n"
          "    bool empty() const;\n"
          "};\n\n\n";

    os << "/// returns a CV object for the specified namespace (prefix);\n"
          "/// currently supported namespaces are: MS UO\n"
          "PWIZ_API_DECL const CV& cv(const std::string& prefix);\n\n\n";

    os << "/// structure for holding CV term info\n" 
          "struct PWIZ_API_DECL CVTermInfo\n"
          "{\n"
          "    CVID cvid;\n"
          "    std::string id;\n"      
          "    std::string name;\n"
          "    std::string def;\n"
          "\n"
          "    typedef std::vector<CVID> id_list;\n"
          "    id_list parentsIsA;\n"
          "    id_list parentsPartOf;\n"
          "    std::vector<std::string> exactSynonyms;\n"
          "\n"
          "    CVTermInfo() : cvid((CVID)-1) {}\n"
          "    const std::string& shortName() const;\n"
          "    std::string prefix() const;\n"
          "};\n\n\n";

    os << "/// returns CV term info for the specified CVID\n" 
          "PWIZ_API_DECL const CVTermInfo& cvTermInfo(CVID cvid);\n\n\n";

    os << "/// returns CV term info for the specified id (accession number)\n" 
          "PWIZ_API_DECL const CVTermInfo& cvTermInfo(const std::string& id);\n\n\n";

    os << "/// returns true iff child IsA parent in the CV\n" 
          "PWIZ_API_DECL bool cvIsA(CVID child, CVID parent);\n\n\n";

    os << "/// returns vector of all valid CVIDs\n" 
          "PWIZ_API_DECL const std::vector<CVID>& cvids();\n\n\n";

    namespaceEnd(os, basename);

    os << "#endif // " << includeGuard << "\n\n\n";
}


// OBO format has some escape characters that C++ doesn't,
// so we double-escape them:
// http://www.geneontology.org/GO.format.obo-1_2.shtml#S.1.5
string escape_copy(const string& str)
{
    string copy(str);
    bal::replace_all(copy, "\\!", "\\\\!");
    bal::replace_all(copy, "\\:", "\\\\:");
    bal::replace_all(copy, "\\,", "\\\\,");
    bal::replace_all(copy, "\\(", "\\\\(");
    bal::replace_all(copy, "\\)", "\\\\)");
    bal::replace_all(copy, "\\[", "\\\\[");
    bal::replace_all(copy, "\\]", "\\\\]");
    bal::replace_all(copy, "\\{", "\\\\{");
    bal::replace_all(copy, "\\}", "\\\\}");
    return copy;
}


void writeCpp(const vector<OBO>& obos, const string& basename, const bfs::path& outputDir)
{
    string filename = basename + ".cpp";
    bfs::path filenameFullPath = outputDir / filename;
    bfs::ofstream os(filenameFullPath, ios::binary);

    writeCopyright(os, filename);

    os << "#define PWIZ_SOURCE\n\n"
       << "#include \"" << basename << ".hpp\"\n"
       << "#include \"pwiz/utility/misc/String.hpp\"\n"
       << "#include \"pwiz/utility/misc/Container.hpp\"\n"
       << "#include \"pwiz/utility/misc/Exception.hpp\"\n"
       << "\n\n";

    namespaceBegin(os, basename);

    os << "namespace {\n\n\n";

    os << "struct TermInfo\n"
          "{\n"
          "    CVID cvid;\n"
          "    const char* id;\n"
          "    const char* name;\n"
          "    const char* def;\n"
          "};\n\n\n";

    os << "const TermInfo termInfos_[] =\n{\n";
    os << "    {CVID_Unknown, \"??:0000000\", \"CVID_Unknown\", \"CVID_Unknown\"},\n";
    for (vector<OBO>::const_iterator obo=obos.begin(); obo!=obos.end(); ++obo)
    for (vector<Term>::const_iterator it=obo->terms.begin(); it!=obo->terms.end(); ++it)
        os << "    {" << enumName(*it) << ", "
           << "\"" << it->prefix << ":" << setw(7) << setfill('0') << it->id << "\", "
           << "\"" << escape_copy(it->name) << "\", " 
           << "\"" << escape_copy(it->def) << "\""
           << "},\n";
    os << "}; // termInfos_\n\n\n";

    os << "const size_t termInfosSize_ = sizeof(termInfos_)/sizeof(TermInfo);\n\n\n";

    os << "struct CVIDPair\n"
          "{\n"
          "    CVID first;\n"
          "    CVID second;\n"
          "};\n\n\n";

    // create a term map for each OBO

    vector< map<Term::id_type, const Term*> > termMaps(obos.size());
    for (vector<OBO>::const_iterator obo=obos.begin(); obo!=obos.end(); ++obo)    
    for (vector<Term>::const_iterator it=obo->terms.begin(); it!=obo->terms.end(); ++it)
        termMaps[obo-obos.begin()][it->id] = &*it;

    os << "CVIDPair relationsIsA_[] =\n{\n";
    for (vector<OBO>::const_iterator obo=obos.begin(); obo!=obos.end(); ++obo)    
    for (vector<Term>::const_iterator it=obo->terms.begin(); it!=obo->terms.end(); ++it)
    for (Term::id_list::const_iterator jt=it->parentsIsA.begin(); jt!=it->parentsIsA.end(); ++jt)
        os << "    {" << enumName(*it) << ", " 
           << enumName(*termMaps[obo-obos.begin()][*jt]) << "},\n";
    os << "}; // relationsIsA_\n\n\n";

    os << "const size_t relationsIsASize_ = sizeof(relationsIsA_)/sizeof(CVIDPair);\n\n\n";

    os << "CVIDPair relationsPartOf_[] =\n{\n";
    for (vector<OBO>::const_iterator obo=obos.begin(); obo!=obos.end(); ++obo)    
    for (vector<Term>::const_iterator it=obo->terms.begin(); it!=obo->terms.end(); ++it)
    for (Term::id_list::const_iterator jt=it->parentsPartOf.begin(); jt!=it->parentsPartOf.end(); ++jt)
        os << "    {" << enumName(*it) << ", " 
           << enumName(*termMaps[obo-obos.begin()][*jt]) << "},\n";
    os << "}; // relationsPartOf_\n\n\n";

    os << "const size_t relationsPartOfSize_ = sizeof(relationsPartOf_)/sizeof(CVIDPair);\n\n\n";

    os << "struct CVIDStringPair\n"
          "{\n"
          "    CVID first;\n"
          "    const char* second;\n"
          "};\n\n\n";

    os << "CVIDStringPair relationsExactSynonym_[] =\n"
       << "{\n"
       << "    {CVID_Unknown, \"Unknown\"},\n";
    for (vector<OBO>::const_iterator obo=obos.begin(); obo!=obos.end(); ++obo)    
    for (vector<Term>::const_iterator it=obo->terms.begin(); it!=obo->terms.end(); ++it)
    for (vector<string>::const_iterator jt=it->exactSynonyms.begin(); jt!=it->exactSynonyms.end(); ++jt)
        os << "    {" << enumName(*it) << ", " 
           << "\"" << *jt << "\"" << "},\n";
    os << "}; // relationsExactSynonym_\n\n\n";

    os << "const size_t relationsExactSynonymSize_ = sizeof(relationsExactSynonym_)/sizeof(CVIDStringPair);\n\n\n";

    os << "bool initialized_ = false;\n"
          "map<CVID,CVTermInfo> infoMap_;\n"
          "map<string,CV> cvMap_;\n"
          "vector<CVID> cvids_;\n"
          "\n\n";

    os << "void initialize()\n"
          "{\n"
          "    for (const TermInfo* it=termInfos_; it!=termInfos_+termInfosSize_; ++it)\n" 
          "    {\n"
          "        CVTermInfo temp;\n"
          "        temp.cvid = it->cvid;\n"
          "        temp.id = it->id;\n"
          "        temp.name = it->name;\n"
          "        temp.def = it->def;\n"
          "        infoMap_[temp.cvid] = temp;\n"
          "        cvids_.push_back(it->cvid);\n"
          "    }\n"
          "\n"
          "    for (const CVIDPair* it=relationsIsA_; it!=relationsIsA_+relationsIsASize_; ++it)\n"
          "        infoMap_[it->first].parentsIsA.push_back(it->second);\n"
          "\n"
          "    for (const CVIDPair* it=relationsPartOf_; it!=relationsPartOf_+relationsPartOfSize_; ++it)\n"
          "        infoMap_[it->first].parentsPartOf.push_back(it->second);\n"
          "\n"
          "    for (const CVIDStringPair* it=relationsExactSynonym_; it!=relationsExactSynonym_+relationsExactSynonymSize_; ++it)\n"
          "        infoMap_[it->first].exactSynonyms.push_back(it->second);\n"
          "\n";

    // TODO: is there a way to get these from the OBOs?
    os << "    cvMap_[\"MS\"].fullName = \"Proteomics Standards Initiative Mass Spectrometry Ontology\";\n"
          "    cvMap_[\"MS\"].URI = \"http://psidev.cvs.sourceforge.net/*checkout*/psidev/psi/psi-ms/mzML/controlledVocabulary/psi-ms.obo\";\n"
          "\n"
          "    cvMap_[\"UO\"].fullName = \"Unit Ontology\";\n"
          "    cvMap_[\"UO\"].URI = \"http://obo.cvs.sourceforge.net/*checkout*/obo/obo/ontology/phenotype/unit.obo\";\n"
          "\n";

    // populate CV ids and versions from OBO headers
    for (vector<OBO>::const_iterator obo=obos.begin(); obo!=obos.end(); ++obo)
    {
        os << "    cvMap_[\"" << obo->prefix << "\"].id = \"" << obo->prefix << "\";\n";

        string version;
        for (size_t i=0; i < obo->header.size(); ++i)
        {
            boost::regex e(".*?[^-]version: (\\S+)");
            boost::smatch what;
            if (regex_match(obo->header[i], what, e))
            {
                version = what[1];
                break;
            }

            if (version.empty())
            {
                boost::regex e("\\s*date: (\\S+).*");
                boost::smatch what;
                if (regex_match(obo->header[i], what, e))
                    version = what[1];
            }
        }

        if (version.empty())
            version = "unknown";

        os << "    cvMap_[\"" << obo->prefix << "\"].version = \"" << version << "\";\n\n";
    }

    os << "    initialized_ = true;\n"
          "}\n\n\n";

    os << "const char* oboPrefixes_[] =\n"
          "{\n";
    for (vector<OBO>::const_iterator obo=obos.begin(); obo!=obos.end(); ++obo)
        os << "    \"" << obo->prefix << "\",\n";
    os << "};\n\n\n";

    os << "const size_t oboPrefixesSize_ = sizeof(oboPrefixes_)/sizeof(const char*);\n\n\n"

          "const size_t enumBlockSize_ = " << enumBlockSize_ << ";\n\n\n"

          "struct StringEquals\n"
          "{\n"
          "    bool operator()(const string& yours) {return mine==yours;}\n"
          "    string mine;\n"
          "    StringEquals(const string& _mine) : mine(_mine) {}\n"
          "};\n\n\n";

    os << "} // namespace\n\n\n";

    os << "PWIZ_API_DECL bool CV::operator==(const CV& that) const\n"
          "{\n"
          "    return id == that.id && fullName == that.fullName && URI == that.URI && version == that.version;\n"
          "}\n\n\n";

    os << "PWIZ_API_DECL bool CV::empty() const\n"
          "{\n"
          "    return id.empty() && fullName.empty() && URI.empty() && version.empty();\n"
          "}\n\n\n";

    os << "PWIZ_API_DECL const CV& cv(const string& prefix)\n"
          "{\n"
          "    if (!initialized_) initialize();\n"
          "    return cvMap_[prefix];\n"
          "}\n\n\n";

    os << "PWIZ_API_DECL const string& CVTermInfo::shortName() const\n"
          "{\n"
          "    const string* result = &name;\n"
          "    for (vector<string>::const_iterator it=exactSynonyms.begin(); it!=exactSynonyms.end(); ++it)\n"
          "        if (result->size() > it->size())\n"
          "            result = &*it;\n"
          "    return *result;\n"
          "}\n\n\n";

    os << "PWIZ_API_DECL string CVTermInfo::prefix() const\n"
          "{\n"
          "    return id.substr(0, id.find_first_of(\":\"));\n"
          "}\n\n\n";

    os << "PWIZ_API_DECL const CVTermInfo& cvTermInfo(CVID cvid)\n"
          "{\n"
          "   if (!initialized_) initialize();\n"
          "   return infoMap_[cvid];\n"
          "}\n\n\n";

    os << "inline unsigned int stringToCVID(const std::string& str)\n"
          "{\n"
          "    errno = 0;\n"
          "    const char* stringToConvert = str.c_str();\n"
          "    const char* endOfConversion = stringToConvert;\n"
          "    unsigned int value = (unsigned int) strtoul (stringToConvert, const_cast<char**>(&endOfConversion), 10);\n"
          "    if (( value == 0u && stringToConvert == endOfConversion) || // error: conversion could not be performed\n"
          "        errno != 0 ) // error: overflow or underflow\n"
          "        throw bad_lexical_cast();\n"
          "    return value;\n"
          "}\n\n\n";

    os << "PWIZ_API_DECL const CVTermInfo& cvTermInfo(const string& id)\n"
          "{\n"
          "    if (!initialized_) initialize();\n"
          "    CVID cvid = CVID_Unknown;\n"
          "\n"
          "    vector<string> tokens;\n"
          "    tokens.reserve(2);\n"
          "    bal::split(tokens, id, bal::is_any_of(\":\"));\n"
          "    if (tokens.size() != 2)\n"
          "        throw runtime_error(\"[cvinfo] Error splitting id \\\"\" + id + \"\\\" into prefix and numeric components\");\n"
          "    const string& prefix = tokens[0];\n"
          "    const string& cvidStr = tokens[1];\n"
          "\n"
          "    const char** it = find_if(oboPrefixes_, oboPrefixes_+oboPrefixesSize_,\n"
          "                              StringEquals(prefix.c_str()));\n"
          "\n"
          "    if (it != oboPrefixes_+oboPrefixesSize_)\n"
          "       cvid = (CVID)((it-oboPrefixes_)*enumBlockSize_ + stringToCVID(cvidStr));\n"
          "\n"
          "    return infoMap_[cvid];\n"
          "}\n\n\n";

    os << "PWIZ_API_DECL bool cvIsA(CVID child, CVID parent)\n"
          "{\n"
          "    if (child == parent) return true;\n"
          "    const CVTermInfo& info = cvTermInfo(child);\n"
          "    for (CVTermInfo::id_list::const_iterator it=info.parentsIsA.begin(); it!=info.parentsIsA.end(); ++it)\n"
          "        if (cvIsA(*it,parent)) return true;\n"
          "    return false;\n"
          "}\n\n\n";

    os << "PWIZ_API_DECL const vector<CVID>& cvids()\n"
          "{\n"
          "   if (!initialized_) initialize();\n"
          "   return cvids_;\n"
          "}\n\n\n";

    namespaceEnd(os, basename);
}


void generateFiles(const vector<OBO>& obos, const string& basename, const bfs::path& outputDir)
{
    writeHpp(obos, basename, outputDir);
    writeCpp(obos, basename, outputDir);
}


int main(int argc, char* argv[])
{
    if (argc < 2)
    {
        cout << "Usage: cvgen file.obo [...]\n";
        cout << "Parse input file(s) and output cv.hpp and cv.cpp.\n";
        return 1;
    }

    try
    {
        bfs::path exeDir(bfs::path(argv[0]).branch_path());

        vector<OBO> obos;
        for (int i=1; i<argc; i++)
            obos.push_back(OBO(argv[i]));

        generateFiles(obos, "cv", exeDir);

        return 0;
    }
    catch (exception& e)
    {
        cerr << "Caught exception: " << e.what() << endl;
    }
    catch (...)
    {
        cerr << "Caught unknown exception.\n";
    }

    return 1; 
}

