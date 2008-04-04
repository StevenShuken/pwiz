//
// MagnitudeLorentzian.cpp
//
//
// Original author: Darren Kessner <Darren.Kessner@cshs.org>
//
// Copyright 2006 Louis Warschaw Prostate Cancer Center
//   Cedars Sinai Medical Center, Los Angeles, California  90048
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


#include "MagnitudeLorentzian.hpp"
#include "utility/math/Parabola.hpp"
#include <cmath>
#include <algorithm>
#include <iostream>
#include <iterator>
#include <stdexcept>


namespace pwiz {
namespace peaks {


using namespace std;
using namespace math;


MagnitudeLorentzian::MagnitudeLorentzian(double a, double b, double c)
:   a_(3), leastSquaresError_(0)
{
    a_[0] = a;
    a_[1] = b;
    a_[2] = c;
}


MagnitudeLorentzian::MagnitudeLorentzian(vector<double> a)
:   a_(a), leastSquaresError_(0)
{
    if (a_.size() != 3)
        throw logic_error("[MagnitudeLorentzian::MagnitudeLorentzian()] 3 coefficients required.");
}


namespace {

pair<double,double> inverseSquare(const pair<double,double>& point)
{
    return make_pair(point.first, 1/(point.second*point.second));
}

double weight(const pair<double,double>& point)
{
    return pow(point.second,6)/4;
}

double calculateLeastSquaresError(const MagnitudeLorentzian& ml, 
                                  const vector< pair<double,double> >& samples)
{
    double result = 0;
    
    for (vector< pair<double,double> >::const_iterator it=samples.begin(); it!=samples.end(); ++it)
    {
        double diff = it->second - ml(it->first); 
        result += diff*diff;
    }

    return result;
}

} // namespace


MagnitudeLorentzian::MagnitudeLorentzian(const vector< pair<double,double> >& samples)
:   leastSquaresError_(0)
{
    vector< pair<double,double> > transformedSamples;
    transform(samples.begin(), samples.end(), back_inserter(transformedSamples), inverseSquare);

    vector<double> weights;
    transform(samples.begin(), samples.end(), back_inserter(weights), weight);

    Parabola p(transformedSamples, weights);
    a_ = p.coefficients();

    leastSquaresError_ = calculateLeastSquaresError(*this, samples);
}


double MagnitudeLorentzian::leastSquaresError() const 
{
    return leastSquaresError_;
}


vector<double>& MagnitudeLorentzian::coefficients()
{
    return a_;
}


const vector<double>& MagnitudeLorentzian::coefficients() const
{
    return a_;
}


double MagnitudeLorentzian::operator()(double x) const
{
    return 1/sqrt(a_[0]*x*x + a_[1]*x + a_[2]);
}


double MagnitudeLorentzian::center() const
{
    return -a_[1]/(2*a_[0]);
}


double MagnitudeLorentzian::tau() const
{
    return operator()(center())/alpha();
}


double MagnitudeLorentzian::alpha() const
{
    return 2*M_PI/sqrt(a_[0]);
}


} // namespace peaks
} // namespace pwiz

