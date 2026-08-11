#ifndef WEBGPU_WATER_OCEAN_APERIODIC_INCLUDED
#define WEBGPU_WATER_OCEAN_APERIODIC_INCLUDED

// Shared coordinate contract for the Lutz/Schoentgen/Gilet ocean synthesis. The FFT output is
// already a zero-mean Gaussian vector field, so Heitz-Neyret histogram preservation reduces to
// variance preservation: divide the barycentric blend by sqrt(sum(weights^2)).

static const float OCEAN_APERIODIC_GRID_SCALE = 3.46410161514; // 2 * sqrt(3)
static const float OCEAN_APERIODIC_SKEW_X = -0.57735026919;    // -1 / sqrt(3)
static const float OCEAN_APERIODIC_SKEW_Y = 1.15470053838;     //  2 / sqrt(3)
static const float OCEAN_APERIODIC_INV_SKEW_Y = 0.86602540378; // sqrt(3) / 2
static const float OCEAN_APERIODIC_HASH_SCALE = 43758.5453;
static const float4 OCEAN_APERIODIC_HASH_MATRIX = float4(127.1, 311.7, 269.5, 183.3);
static const float OCEAN_APERIODIC_WEIGHT_EPSILON = 1e-6;
static const float OCEAN_APERIODIC_DIRECTION_EPSILON = 1e-6;

struct OceanAperiodicTriangle
{
    float3 weights;
    int2 vertex0;
    int2 vertex1;
    int2 vertex2;
};

float2 OceanAperiodicSkew(float2 gridPosition)
{
    return float2(gridPosition.x + OCEAN_APERIODIC_SKEW_X * gridPosition.y,
                  OCEAN_APERIODIC_SKEW_Y * gridPosition.y);
}

float2 OceanAperiodicUnskew(float2 skewedPosition)
{
    float y = skewedPosition.y * OCEAN_APERIODIC_INV_SKEW_Y;
    return float2(skewedPosition.x - OCEAN_APERIODIC_SKEW_X * y, y);
}

OceanAperiodicTriangle OceanAperiodicTriangleAt(float2 exemplarUv, float tileScale)
{
    float safeTileScale = max(tileScale, OCEAN_APERIODIC_WEIGHT_EPSILON);
    float2 gridPosition = exemplarUv * (OCEAN_APERIODIC_GRID_SCALE / safeTileScale);
    float2 skewed = OceanAperiodicSkew(gridPosition);
    int2 baseVertex = (int2)floor(skewed);
    float2 local = frac(skewed);
    float thirdWeight = 1.0 - local.x - local.y;

    OceanAperiodicTriangle tileTriangle;
    if (thirdWeight > 0.0)
    {
        tileTriangle.weights = float3(thirdWeight, local.y, local.x);
        tileTriangle.vertex0 = baseVertex;
        tileTriangle.vertex1 = baseVertex + int2(0, 1);
        tileTriangle.vertex2 = baseVertex + int2(1, 0);
    }
    else
    {
        tileTriangle.weights = float3(-thirdWeight, 1.0 - local.y, 1.0 - local.x);
        tileTriangle.vertex0 = baseVertex + int2(1, 1);
        tileTriangle.vertex1 = baseVertex + int2(1, 0);
        tileTriangle.vertex2 = baseVertex + int2(0, 1);
    }
    return tileTriangle;
}

float2 OceanAperiodicHash(int2 vertex)
{
    float2 p = (float2)vertex;
    float2 projected = float2(dot(p, OCEAN_APERIODIC_HASH_MATRIX.xy),
                              dot(p, OCEAN_APERIODIC_HASH_MATRIX.zw));
    return frac(sin(projected) * OCEAN_APERIODIC_HASH_SCALE);
}

float2 OceanAperiodicVertexUv(int2 vertex, float tileScale)
{
    float2 gridPosition = OceanAperiodicUnskew((float2)vertex);
    return gridPosition * (tileScale / OCEAN_APERIODIC_GRID_SCALE);
}

float3 OceanAperiodicVarianceWeights(float3 barycentricWeights)
{
    return barycentricWeights * rsqrt(max(dot(barycentricWeights, barycentricWeights),
                                          OCEAN_APERIODIC_WEIGHT_EPSILON));
}

float2 OceanAperiodicRotate(float2 value, float angle)
{
    float sineAngle;
    float cosineAngle;
    sincos(angle, sineAngle, cosineAngle);
    return float2(cosineAngle * value.x - sineAngle * value.y,
                  sineAngle * value.x + cosineAngle * value.y);
}

// Direction maps use the conventional signed-vector encoding: RG [0,1] -> XY [-1,1].
// A zero-length vector means no authored rotation, which also makes neutral fallback textures safe.
float OceanAperiodicDirectionAngle(float2 encodedDirection, float strength)
{
    float2 direction = encodedDirection * 2.0 - 1.0;
    float directionLengthSquared = dot(direction, direction);
    if (directionLengthSquared <= OCEAN_APERIODIC_DIRECTION_EPSILON) return 0.0;
    return atan2(direction.y, direction.x) * saturate(strength);
}

#endif
