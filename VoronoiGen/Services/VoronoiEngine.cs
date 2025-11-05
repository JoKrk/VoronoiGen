using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoronoiGen.Models;

namespace VoronoiGen.Services
{
    /// <summary>
    /// I want a voronoi generation algoritihm that takes a boundary with internal contours (holes) and then generates a voronoi diagram
    /// that fits within that boundary, respecting the holes. The algorithm should also support Lloyd relaxation to improve the uniformity of the cells.
    /// Also there will be options for offsetting the cells inwards or outwards by a specified distance, using polygon offsetting techniques. These 
    /// will not clip the cells to the boundary, but rather adjust their shapes while keeping them within the overall offsetted boundary and holes.
    /// </summary>
    public static class VoronoiEngine
    {


    }

}