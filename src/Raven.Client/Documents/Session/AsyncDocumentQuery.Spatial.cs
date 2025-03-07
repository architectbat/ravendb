﻿using System;
using System.Linq.Expressions;
using Raven.Client.Documents.Indexes.Spatial;
using Raven.Client.Documents.Queries.Spatial;
using Raven.Client.Extensions;

namespace Raven.Client.Documents.Session
{
    public partial class AsyncDocumentQuery<T>
    {
        /// <inheritdoc />
        IAsyncDocumentQuery<T> IFilterDocumentQueryBase<T, IAsyncDocumentQuery<T>>.Spatial(Expression<Func<T, object>> path, Func<SpatialCriteriaFactory, SpatialCriteria> clause)
        {
            var criteria = clause(SpatialCriteriaFactory.Instance);
            Spatial(path.ToPropertyPath(Conventions), criteria);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IFilterDocumentQueryBase<T, IAsyncDocumentQuery<T>>.Spatial(string fieldName, Func<SpatialCriteriaFactory, SpatialCriteria> clause)
        {
            var criteria = clause(SpatialCriteriaFactory.Instance);
            Spatial(fieldName, criteria);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IFilterDocumentQueryBase<T, IAsyncDocumentQuery<T>>.Spatial(DynamicSpatialField field, Func<SpatialCriteriaFactory, SpatialCriteria> clause)
        {
            var criteria = clause(SpatialCriteriaFactory.Instance);
            Spatial(field, criteria);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IFilterDocumentQueryBase<T, IAsyncDocumentQuery<T>>.Spatial(Func<DynamicSpatialFieldFactory<T>, DynamicSpatialField> field, Func<SpatialCriteriaFactory, SpatialCriteria> clause)
        {
            var criteria = clause(SpatialCriteriaFactory.Instance);
            var dynamicField = field(new DynamicSpatialFieldFactory<T>(Conventions));
            Spatial(dynamicField, criteria);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IFilterDocumentQueryBase<T, IAsyncDocumentQuery<T>>.WithinRadiusOf<TValue>(Expression<Func<T, TValue>> propertySelector, double radius, double latitude, double longitude, SpatialUnits? radiusUnits, double distanceErrorPct)
        {
            WithinRadiusOf(propertySelector.ToPropertyPath(Conventions), radius, latitude, longitude, radiusUnits, distanceErrorPct);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IFilterDocumentQueryBase<T, IAsyncDocumentQuery<T>>.WithinRadiusOf(string fieldName, double radius, double latitude, double longitude, SpatialUnits? radiusUnits, double distanceErrorPct)
        {
            WithinRadiusOf(fieldName, radius, latitude, longitude, radiusUnits, distanceErrorPct);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IFilterDocumentQueryBase<T, IAsyncDocumentQuery<T>>.RelatesToShape<TValue>(Expression<Func<T, TValue>> propertySelector, string shapeWkt, SpatialRelation relation, double distanceErrorPct)
        {
            Spatial(propertySelector.ToPropertyPath(Conventions), shapeWkt, relation, null, distanceErrorPct);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IFilterDocumentQueryBase<T, IAsyncDocumentQuery<T>>.RelatesToShape<TValue>(Expression<Func<T, TValue>> propertySelector, string shapeWkt, SpatialRelation relation, SpatialUnits units, double distanceErrorPct)
        {
            Spatial(propertySelector.ToPropertyPath(Conventions), shapeWkt, relation, units, distanceErrorPct);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IFilterDocumentQueryBase<T, IAsyncDocumentQuery<T>>.RelatesToShape(string fieldName, string shapeWkt, SpatialRelation relation, double distanceErrorPct)
        {
            Spatial(fieldName, shapeWkt, relation, null, distanceErrorPct);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IFilterDocumentQueryBase<T, IAsyncDocumentQuery<T>>.RelatesToShape(string fieldName, string shapeWkt, SpatialRelation relation, SpatialUnits units, double distanceErrorPct)
        {
            Spatial(fieldName, shapeWkt, relation, units, distanceErrorPct);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistance(DynamicSpatialField field, double latitude, double longitude)
        {
            OrderByDistance(field, latitude, longitude);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistance(Func<DynamicSpatialFieldFactory<T>, DynamicSpatialField> field, double latitude, double longitude)
        {
            OrderByDistance(field(new DynamicSpatialFieldFactory<T>(Conventions)), latitude, longitude);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistance(DynamicSpatialField field, string shapeWkt)
        {
            OrderByDistance(field, shapeWkt);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistance(Func<DynamicSpatialFieldFactory<T>, DynamicSpatialField> field, string shapeWkt)
        {
            OrderByDistance(field(new DynamicSpatialFieldFactory<T>(Conventions)), shapeWkt);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistance(Expression<Func<T, object>> propertySelector, double latitude, double longitude)
        {
            OrderByDistance(propertySelector.ToPropertyPath(Conventions), latitude, longitude);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistance(string fieldName, double latitude, double longitude)
        {
            OrderByDistance(fieldName, latitude, longitude);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistance(Expression<Func<T, object>> propertySelector, string shapeWkt)
        {
            OrderByDistance(propertySelector.ToPropertyPath(Conventions), shapeWkt);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistance(string fieldName, string shapeWkt)
        {
            OrderByDistance(fieldName, shapeWkt);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistanceDescending(DynamicSpatialField field, double latitude, double longitude)
        {
            OrderByDistanceDescending(field, latitude, longitude);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistanceDescending(Func<DynamicSpatialFieldFactory<T>, DynamicSpatialField> field, double latitude, double longitude)
        {
            OrderByDistanceDescending(field(new DynamicSpatialFieldFactory<T>(Conventions)), latitude, longitude);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistanceDescending(DynamicSpatialField field, string shapeWkt)
        {
            OrderByDistanceDescending(field, shapeWkt);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistanceDescending(Func<DynamicSpatialFieldFactory<T>, DynamicSpatialField> field, string shapeWkt)
        {
            OrderByDistanceDescending(field(new DynamicSpatialFieldFactory<T>(Conventions)), shapeWkt);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistanceDescending(Expression<Func<T, object>> propertySelector, double latitude, double longitude)
        {
            OrderByDistanceDescending(propertySelector.ToPropertyPath(Conventions), latitude, longitude);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistanceDescending(string fieldName, double latitude, double longitude)
        {
            OrderByDistanceDescending(fieldName, latitude, longitude);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistanceDescending(Expression<Func<T, object>> propertySelector, string shapeWkt)
        {
            OrderByDistanceDescending(propertySelector.ToPropertyPath(Conventions), shapeWkt);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistanceDescending(string fieldName, string shapeWkt)
        {
            OrderByDistanceDescending(fieldName, shapeWkt);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistance(Expression<Func<T, object>> propertySelector, double latitude, double longitude, double roundFactor)
        {
            OrderByDistance(propertySelector.ToPropertyPath(Conventions), latitude, longitude, roundFactor);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistance(string fieldName, double latitude, double longitude, double roundFactor)
        {
            OrderByDistance(fieldName, latitude, longitude, roundFactor);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistance(Expression<Func<T, object>> propertySelector, string shapeWkt, double roundFactor)
        {
            OrderByDistance(propertySelector.ToPropertyPath(Conventions), shapeWkt, roundFactor);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistance(string fieldName, string shapeWkt, double roundFactor)
        {
            OrderByDistance(fieldName, shapeWkt, roundFactor);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistanceDescending(Expression<Func<T, object>> propertySelector, double latitude, double longitude, double roundFactor)
        {
            OrderByDistanceDescending(propertySelector.ToPropertyPath(Conventions), latitude, longitude, roundFactor);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistanceDescending(string fieldName, double latitude, double longitude, double roundFactor)
        {
            OrderByDistanceDescending(fieldName, latitude, longitude, roundFactor);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistanceDescending(Expression<Func<T, object>> propertySelector, string shapeWkt, double roundFactor)
        {
            OrderByDistanceDescending(propertySelector.ToPropertyPath(Conventions), shapeWkt, roundFactor);
            return this;
        }

        /// <inheritdoc />
        IAsyncDocumentQuery<T> IDocumentQueryBase<T, IAsyncDocumentQuery<T>>.OrderByDistanceDescending(string fieldName, string shapeWkt, double roundFactor)
        {
            OrderByDistanceDescending(fieldName, shapeWkt, roundFactor);
            return this;
        }
    }
}
